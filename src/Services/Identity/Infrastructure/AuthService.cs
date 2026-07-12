using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Identity;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Domain.Tenancy;
using ThreeCommerce.Identity.Infrastructure.Security;

namespace ThreeCommerce.Identity.Infrastructure;

/// <summary>
/// Custom auth flows on vetted primitives (ADR-0012). Events (verification/reset
/// emails) publish through the EF outbox in the same transaction as the state change.
/// </summary>
public sealed class AuthService(
    IdentityDbContext db,
    IPasswordHasher passwordHasher,
    IPublishEndpoint publisher,
    TimeProvider time,
    IdentityBootstrapper bootstrapper,
    MfaPlatformPolicy platformMfa,
    ILogger<AuthService> logger) : IAuthService
{
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);
    private static readonly TimeSpan VerifyTokenLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);
    private const int LockoutThreshold = 5;
    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);

    public async Task<RegisterResult> RegisterAsync(string email, string password, MemberProfile? profile, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var tenant = await bootstrapper.EnsureDefaultTenantAsync(ct);
        // Tenant-scoped RLS context for the Users write (ADR-0024). Tenants/Roles/Permissions are
        // not RLS'd, so the seeding below is unaffected by the scope.
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.ForTenant(tenant.Id), ct);
        await bootstrapper.SeedPermissionRegistryAsync(tenant.Id, ct);

        var existing = await db.Users.SingleOrDefaultAsync(u => u.TenantId == tenant.Id && u.Email == normalized, ct);
        if (existing is not null)
        {
            // No enumeration: same outcome shape; no email sent (silent no-op).
            logger.LogInformation("Registration attempt for existing email (suppressed)");
            return new RegisterResult(IsNewUser: false);
        }

        var now = time.GetUtcNow();
        var principal = new Principal
        {
            Id = Guid.CreateVersion7(),
            Type = PrincipalType.Human,
            DisplayName = normalized,
            CreatedAt = now,
        };
        db.Principals.Add(principal);

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            PrincipalId = principal.Id,
            Email = normalized,
            PasswordHash = passwordHasher.Hash(password),
            Title = Trim(profile?.Title),
            FirstName = Trim(profile?.FirstName),
            MiddleName = Trim(profile?.MiddleName),
            LastName = Trim(profile?.LastName),
            PreferredName = Trim(profile?.PreferredName),
            Phone = Trim(profile?.Phone),
            DateOfBirth = profile?.DateOfBirth,
            MarketingConsent = profile?.MarketingConsent ?? false,
            CreatedAt = now,
        };
        db.Users.Add(user);

        var membership = await bootstrapper.EnsureMembershipAsync(tenant.Id, principal, MembershipKind.Customer, isTenantOwner: false, ct);
        await bootstrapper.AssignRoleAsync(membership, Roles.Customer, ct);

        var rawToken = CreateEmailToken(user.Id, EmailTokenPurpose.VerifyEmail, VerifyTokenLifetime, now);
        await publisher.Publish(new UserRegistered(user.Id, user.Email, rawToken), ct);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        return new RegisterResult(IsNewUser: true);
    }

    public async Task<LoginResult?> LoginAsync(string email, string password, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var now = time.GetUtcNow();
        var tenant = await bootstrapper.EnsureDefaultTenantAsync(ct);
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.ForTenant(tenant.Id), ct);
        await bootstrapper.SeedPermissionRegistryAsync(tenant.Id, ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.TenantId == tenant.Id && u.Email == normalized, ct);

        if (user is null)
        {
            // Burn comparable time so missing-vs-wrong-password is not distinguishable.
            passwordHasher.Verify(password, passwordHasher.Hash("timing-equalizer"));
            return null;
        }

        if (user.LockoutUntil is { } until && until > now)
        {
            return null;
        }

        if (!passwordHasher.Verify(password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= LockoutThreshold)
            {
                var minutes = Math.Min(
                    Math.Pow(2, user.FailedLoginCount - LockoutThreshold),
                    MaxLockout.TotalMinutes);
                user.LockoutUntil = now.AddMinutes(minutes);
            }

            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return null;
        }

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;

        var principal = await bootstrapper.EnsureHumanPrincipalForUserAsync(user, ct);
        var membership = await bootstrapper.EnsureMembershipAsync(
            tenant.Id,
            principal,
            user.Role == Roles.Admin ? MembershipKind.Staff : MembershipKind.Customer,
            isTenantOwner: user.Role == Roles.Admin,
            ct);
        await bootstrapper.AssignRoleAsync(membership, user.Role, ct);

        // A confirmed factor is ALWAYS challenged (voluntary enrollment included); the policy only
        // governs whether unenrolled users are told to enroll (mt6_10 GOTCHA: required-but-unenrolled
        // logs in with a nag, never a block — the bootstrap admin must not be lockable-out before
        // a factor exists).
        var mfaPending = await db.MfaEnrollments.AnyAsync(e => e.UserId == user.Id && e.ConfirmedAt != null, ct);

        var rawToken = OpaqueToken.Generate();
        var session = new Session
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            ClaimsVersion = principal.ClaimsVersion,
            TokenHash = OpaqueToken.HashOf(rawToken),
            CreatedAt = now,
            ExpiresAt = now + SessionLifetime,
            MfaPending = mfaPending,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        return new LoginResult(rawToken, user.Id, user.Role, session.ExpiresAt, mfaPending);
    }

    public async Task LogoutAsync(string rawSessionToken, CancellationToken ct)
    {
        var hash = OpaqueToken.HashOf(rawSessionToken);
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (session is not null && session.RevokedAt is null)
        {
            session.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> VerifyEmailAsync(string rawToken, CancellationToken ct)
    {
        // Secret-keyed, cross-tenant: the token resolves the user. Platform scope so the Users
        // RLS policy permits loading the user regardless of tenant (ADR-0024/0025).
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.Platform(), ct);
        var token = await FindUsableTokenAsync(rawToken, EmailTokenPurpose.VerifyEmail, ct);
        if (token is null)
        {
            return false;
        }

        token.UsedAt = time.GetUtcNow();
        var user = await db.Users.SingleAsync(u => u.Id == token.UserId, ct);
        user.EmailVerified = true;
        // Attach any prior guest orders placed with this (now-verified) email (FR-7).
        await publisher.Publish(new EmailVerified(user.Id, user.Email), ct);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return true;
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var tenant = await bootstrapper.EnsureDefaultTenantAsync(ct);
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.ForTenant(tenant.Id), ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.TenantId == tenant.Id && u.Email == normalized, ct);
        if (user is null)
        {
            return; // no enumeration
        }

        var now = time.GetUtcNow();
        var rawToken = CreateEmailToken(user.Id, EmailTokenPurpose.ResetPassword, ResetTokenLifetime, now);
        await publisher.Publish(new PasswordResetRequested(user.Id, user.Email, rawToken), ct);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
    }

    public async Task<bool> ConfirmPasswordResetAsync(string rawToken, string newPassword, CancellationToken ct)
    {
        // Secret-keyed, cross-tenant (token resolves the user) — platform scope.
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.Platform(), ct);
        var token = await FindUsableTokenAsync(rawToken, EmailTokenPurpose.ResetPassword, ct);
        if (token is null)
        {
            return false;
        }

        var now = time.GetUtcNow();
        token.UsedAt = now;

        var user = await db.Users.SingleAsync(u => u.Id == token.UserId, ct);
        user.PasswordHash = passwordHasher.Hash(newPassword);
        user.FailedLoginCount = 0;
        user.LockoutUntil = null;

        // Credential change revokes every live session (NFR-8 hygiene).
        await foreach (var session in db.Sessions
            .Where(s => s.UserId == user.Id && s.RevokedAt == null)
            .AsAsyncEnumerable().WithCancellation(ct))
        {
            session.RevokedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return true;
    }

    public async Task<SessionInfo?> IntrospectAsync(string rawSessionToken, CancellationToken ct)
    {
        var hash = OpaqueToken.HashOf(rawSessionToken);
        var now = time.GetUtcNow();

        // The session token is global; resolving its user is inherently cross-tenant (the gateway
        // calls this before any tenant scope of its own), so introspection reads under platform
        // scope and the Users RLS policy permits the join (ADR-0024).
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.Platform(), ct);
        var result = await db.Sessions
            // MFA-pending sessions hold no claims anywhere on the platform (mt6_10) — the challenge
            // endpoint resolves the raw cookie itself, exactly like Logout.
            .Where(s => s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now && !s.MfaPending)
            .Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { Session = s, User = u })
            .Join(db.Principals, x => x.User.PrincipalId, p => p.Id, (x, p) => new { x.Session, x.User, Principal = p })
            .Where(x => x.Session.ClaimsVersion == x.Principal.ClaimsVersion && x.Principal.Status == PrincipalStatus.Active)
            .Where(x => x.User.TenantId != null)
            .Select(x => new SessionInfo(
                x.Session.Id, x.User.Id, x.User.TenantId!.Value, x.User.Role, x.User.Email, x.Session.ExpiresAt,
                x.Session.StrongAuthAt, x.Session.StrongAuthAt != null ? "pwd otp" : "pwd"))
            .SingleOrDefaultAsync(ct);

        await scope.CommitAsync(ct);
        return result;
    }

    public async Task<MfaStatus?> GetMfaStatusAsync(string rawSessionToken, CancellationToken ct)
    {
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.Platform(), ct);
        var resolved = await ResolveSessionAsync(rawSessionToken, includePending: true, ct);
        if (resolved is null)
        {
            return null;
        }

        var (session, user) = resolved.Value;

        var enrolled = await db.MfaEnrollments.AnyAsync(e => e.UserId == user.Id && e.ConfirmedAt != null, ct);
        var tenantPolicy = user.TenantId is { } tenantId
            ? await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.MfaPolicy).SingleAsync(ct)
            : MfaRequirement.Disabled;
        var required = new MfaPolicy(platformMfa.Minimum, tenantPolicy).RequiresMfa(user.Role == Roles.Admin);
        await scope.CommitAsync(ct);
        return new MfaStatus(enrolled, session.MfaPending, required);
    }

    public async Task<MfaEnrollmentStart?> BeginMfaEnrollmentAsync(string rawSessionToken, CancellationToken ct)
    {
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.Platform(), ct);
        var resolved = await ResolveSessionAsync(rawSessionToken, includePending: false, ct);
        if (resolved is null)
        {
            return null;
        }

        var user = resolved.Value.User;

        var enrollment = await db.MfaEnrollments.SingleOrDefaultAsync(e => e.UserId == user.Id, ct);
        if (enrollment?.IsConfirmed == true)
        {
            // Resetting a confirmed factor from a mere session would be an account-takeover lever;
            // that path is a deliberate support/recovery flow, not this endpoint.
            return null;
        }

        var secret = Totp.GenerateSecret();
        if (enrollment is null)
        {
            db.MfaEnrollments.Add(new MfaEnrollment
            {
                Id = Guid.CreateVersion7(),
                UserId = user.Id,
                SecretBase32 = secret,
                CreatedAt = time.GetUtcNow(),
            });
        }
        else
        {
            enrollment.SecretBase32 = secret;
        }

        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return new MfaEnrollmentStart(secret, Totp.OtpauthUri("3commerce", user.Email, secret));
    }

    public async Task<IReadOnlyList<string>?> ConfirmMfaEnrollmentAsync(string rawSessionToken, string code, CancellationToken ct)
    {
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.Platform(), ct);
        var resolved = await ResolveSessionAsync(rawSessionToken, includePending: false, ct);
        var now = time.GetUtcNow();
        if (resolved is null)
        {
            return null;
        }

        var (session, user) = resolved.Value;

        var enrollment = await db.MfaEnrollments.SingleOrDefaultAsync(e => e.UserId == user.Id && e.ConfirmedAt == null, ct);
        if (enrollment is null || !Totp.Verify(enrollment.SecretBase32, code, now))
        {
            return null;
        }

        var recoveryCodes = Enumerable.Range(0, 8)
            .Select(_ => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(5)))
            .ToList();
        enrollment.ConfirmedAt = now;
        enrollment.RecoveryCodeHashes = recoveryCodes.Select(OpaqueToken.HashOf).ToList();
        session.StrongAuthAt = now; // possession just proven
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return recoveryCodes;
    }

    public async Task<bool> CompleteMfaChallengeAsync(string rawSessionToken, string code, CancellationToken ct)
    {
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.Platform(), ct);
        var resolved = await ResolveSessionAsync(rawSessionToken, includePending: true, ct);
        var now = time.GetUtcNow();
        if (resolved is null || !resolved.Value.Session.MfaPending)
        {
            return false;
        }

        var (session, user) = resolved.Value;

        // Wrong codes feed the same counter/lockout as wrong passwords — a pending session is
        // not an unthrottled TOTP oracle.
        if (user.LockoutUntil is { } until && until > now)
        {
            return false;
        }

        var enrollment = await db.MfaEnrollments.SingleOrDefaultAsync(e => e.UserId == user.Id && e.ConfirmedAt != null, ct);
        if (enrollment is null)
        {
            return false;
        }

        var codeHash = OpaqueToken.HashOf(code);
        var isRecovery = enrollment.RecoveryCodeHashes.Contains(codeHash);
        if (!isRecovery && !Totp.Verify(enrollment.SecretBase32, code, now))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= LockoutThreshold)
            {
                var minutes = Math.Min(Math.Pow(2, user.FailedLoginCount - LockoutThreshold), MaxLockout.TotalMinutes);
                user.LockoutUntil = now.AddMinutes(minutes);
            }

            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return false;
        }

        if (isRecovery)
        {
            enrollment.RecoveryCodeHashes.Remove(codeHash); // one-time use
        }

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;
        session.MfaPending = false;
        session.StrongAuthAt = now;
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return true;
    }

    public async Task<bool> StepUpAsync(string rawSessionToken, string code, CancellationToken ct)
    {
        await using var scope = await db.BeginTenantScopeAsync(TenantContext.Platform(), ct);
        var resolved = await ResolveSessionAsync(rawSessionToken, includePending: false, ct);
        var now = time.GetUtcNow();
        if (resolved is null)
        {
            return false;
        }

        var (session, user) = resolved.Value;

        var enrollment = await db.MfaEnrollments.SingleOrDefaultAsync(e => e.UserId == user.Id && e.ConfirmedAt != null, ct);
        if (enrollment is null || !Totp.Verify(enrollment.SecretBase32, code, now))
        {
            return false;
        }

        session.StrongAuthAt = now;
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return true;
    }

    /// <summary>Live session + user by raw token; <paramref name="includePending"/> admits MFA-pending sessions.</summary>
    private async Task<(Session Session, User User)?> ResolveSessionAsync(string rawSessionToken, bool includePending, CancellationToken ct)
    {
        var hash = OpaqueToken.HashOf(rawSessionToken);
        var now = time.GetUtcNow();
        var session = await db.Sessions.SingleOrDefaultAsync(
            s => s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now, ct);
        if (session is null || (session.MfaPending && !includePending))
        {
            return null;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == session.UserId, ct);
        return user is null ? null : (session, user);
    }

    private string CreateEmailToken(Guid userId, EmailTokenPurpose purpose, TimeSpan lifetime, DateTimeOffset now)
    {
        var raw = OpaqueToken.Generate();
        db.EmailTokens.Add(new EmailToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = OpaqueToken.HashOf(raw),
            Purpose = purpose,
            ExpiresAt = now + lifetime,
        });
        return raw;
    }

    private async Task<EmailToken?> FindUsableTokenAsync(string rawToken, EmailTokenPurpose purpose, CancellationToken ct)
    {
        var hash = OpaqueToken.HashOf(rawToken);
        var now = time.GetUtcNow();
        return await db.EmailTokens.SingleOrDefaultAsync(
            t => t.TokenHash == hash && t.Purpose == purpose && t.UsedAt == null && t.ExpiresAt > now, ct);
    }
}
