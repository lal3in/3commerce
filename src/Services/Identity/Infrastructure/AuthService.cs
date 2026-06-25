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
    ILogger<AuthService> logger) : IAuthService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);
    private static readonly TimeSpan VerifyTokenLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);
    private const int LockoutThreshold = 5;
    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);

    public async Task<RegisterResult> RegisterAsync(string email, string password, CancellationToken ct)
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

        var rawToken = OpaqueToken.Generate();
        var session = new Session
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            ClaimsVersion = principal.ClaimsVersion,
            TokenHash = OpaqueToken.HashOf(rawToken),
            CreatedAt = now,
            ExpiresAt = now + SessionLifetime,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        return new LoginResult(rawToken, user.Id, user.Role, session.ExpiresAt);
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
            .Where(s => s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now)
            .Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { Session = s, User = u })
            .Join(db.Principals, x => x.User.PrincipalId, p => p.Id, (x, p) => new { x.Session, x.User, Principal = p })
            .Where(x => x.Session.ClaimsVersion == x.Principal.ClaimsVersion && x.Principal.Status == PrincipalStatus.Active)
            .Where(x => x.User.TenantId != null)
            .Select(x => new SessionInfo(x.Session.Id, x.User.Id, x.User.TenantId!.Value, x.User.Role, x.User.Email, x.Session.ExpiresAt))
            .SingleOrDefaultAsync(ct);

        await scope.CommitAsync(ct);
        return result;
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
