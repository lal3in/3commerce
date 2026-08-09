using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.Identity.Infrastructure.Sessions;

/// <summary>
/// Cache-aside for session introspection (ADR-0044). The gateway introspects on every authenticated
/// request; caching the resolved <see cref="SessionInfo"/> by token hash takes that 3-table join off the
/// hot Postgres path. Postgres remains the source of truth: the cache is short-TTL'd AND explicitly
/// invalidated on logout, credential reset, and any ClaimsVersion bump, so a revoked/role-changed session
/// never survives past those events (and never past the TTL as a backstop). Disabled by default
/// (Sessions:Cache:Enabled) — when off, every method is a no-op and introspection always hits Postgres.
///
/// Only fully-authenticated, non-MFA-pending sessions are ever cached (that is all IntrospectAsync
/// resolves), so a pending/again-challenged session is never served from cache.
/// </summary>
public interface ISessionCache
{
    /// <summary>Cached introspection for the token hash, or null on miss / disabled / Redis-unavailable.</summary>
    public Task<SessionInfo?> GetAsync(string tokenHash, CancellationToken ct);

    /// <summary>Caches a freshly-resolved introspection. No-op when disabled or Redis is unavailable.</summary>
    public Task SetAsync(string tokenHash, SessionInfo info, CancellationToken ct);

    /// <summary>Evicts a single session (logout). Needs the user id to also drop it from the user's set.</summary>
    public Task InvalidateTokenAsync(string tokenHash, Guid userId, CancellationToken ct);

    /// <summary>Evicts ALL cached sessions for a user (credential reset, ClaimsVersion bump, deactivation).</summary>
    public Task InvalidateUserAsync(Guid userId, CancellationToken ct);
}

/// <summary>Bound from the <c>Sessions:Cache</c> config section. Off by default (auth-sensitive; ADR-0044).</summary>
public sealed class SessionCacheOptions
{
    public bool Enabled { get; set; }
}
