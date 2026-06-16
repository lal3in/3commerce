using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Identity;
using ThreeCommerce.Identity.Domain;
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
        var existing = await db.Users.SingleOrDefaultAsync(u => u.Email == normalized, ct);
        if (existing is not null)
        {
            // No enumeration: same outcome shape; no email sent (silent no-op).
            logger.LogInformation("Registration attempt for existing email (suppressed)");
            return new RegisterResult(IsNewUser: false);
        }

        var now = time.GetUtcNow();
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = normalized,
            PasswordHash = passwordHasher.Hash(password),
            CreatedAt = now,
        };
        db.Users.Add(user);

        var rawToken = CreateEmailToken(user.Id, EmailTokenPurpose.VerifyEmail, VerifyTokenLifetime, now);
        await publisher.Publish(new UserRegistered(user.Id, user.Email, rawToken), ct);
        await db.SaveChangesAsync(ct);

        return new RegisterResult(IsNewUser: true);
    }

    public async Task<LoginResult?> LoginAsync(string email, string password, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var now = time.GetUtcNow();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == normalized, ct);

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
            return null;
        }

        user.FailedLoginCount = 0;
        user.LockoutUntil = null;

        var rawToken = OpaqueToken.Generate();
        var session = new Session
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            TokenHash = OpaqueToken.HashOf(rawToken),
            CreatedAt = now,
            ExpiresAt = now + SessionLifetime,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

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
        return true;
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken ct)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null)
        {
            return; // no enumeration
        }

        var now = time.GetUtcNow();
        var rawToken = CreateEmailToken(user.Id, EmailTokenPurpose.ResetPassword, ResetTokenLifetime, now);
        await publisher.Publish(new PasswordResetRequested(user.Id, user.Email, rawToken), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> ConfirmPasswordResetAsync(string rawToken, string newPassword, CancellationToken ct)
    {
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
        return true;
    }

    public async Task<SessionInfo?> IntrospectAsync(string rawSessionToken, CancellationToken ct)
    {
        var hash = OpaqueToken.HashOf(rawSessionToken);
        var now = time.GetUtcNow();

        var result = await db.Sessions
            .Where(s => s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now)
            .Join(db.Users, s => s.UserId, u => u.Id,
                (s, u) => new SessionInfo(s.Id, u.Id, u.Role, s.ExpiresAt))
            .SingleOrDefaultAsync(ct);

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
