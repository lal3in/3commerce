using System.Threading.RateLimiting;
using ThreeCommerce.Gateway.Tenancy;

namespace ThreeCommerce.Gateway.RateLimiting;

/// <summary>How Redis-outage should be handled for the distributed limiter (config toggle, ADR-0044).</summary>
public enum RateLimitOutageMode
{
    /// <summary>Fall back to the in-process limiter — don't lock users out over a cache blip. Default.</summary>
    FailOpen,

    /// <summary>Reject (429) until Redis recovers — stricter abuse protection.</summary>
    FailClosed,
}

/// <summary>Which limiter backend the gateway uses.</summary>
public enum RateLimitBackend
{
    /// <summary>Legacy per-instance in-process limiter (each replica gets its own window).</summary>
    InMemory,

    /// <summary>Shared Redis limiter — one combined window across all replicas.</summary>
    Redis,
}

/// <summary>Bound from the <c>RateLimiting</c> config section.</summary>
public sealed class RateLimitOptions
{
    public RateLimitBackend Backend { get; set; } = RateLimitBackend.InMemory;
    public RateLimitOutageMode OnRedisOutage { get; set; } = RateLimitOutageMode.FailOpen;

    /// <summary>
    /// Per-minute permit limit for auth endpoints (login/register/password-reset) against credential
    /// stuffing. Production default is 30; a single-IP environment (dev/E2E, where the whole test suite
    /// shares one client IP + partition) can raise it via <c>RateLimiting:AuthPermitLimit</c> so the
    /// harness doesn't throttle itself — the enforcement path stays identical.
    /// </summary>
    public int AuthPermitLimit { get; set; } = 30;

    /// <summary>Per-minute permit limit for every other (non-auth) endpoint.</summary>
    public int AnyPermitLimit { get; set; } = 1000;
}

/// <summary>
/// The single source of truth for how a request is partitioned and limited — shared by the Redis and the
/// in-process backends so they always agree. Auth endpoints (login/register/password-reset) get a tight
/// per-IP window against credential stuffing; everything else is permissive. Partitioned by
/// tenant + storefront + client IP, exactly as the original in-process limiter did.
/// </summary>
public static class RateLimitPolicy
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public readonly record struct Decision(string Key, string Kind, int PermitLimit, TimeSpan Window);

    public static Decision Resolve(HttpContext context, RateLimitOptions options)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var path = context.Request.Path.Value ?? string.Empty;
        var tenant = context.Request.Headers.TryGetValue(DomainResolutionMiddleware.TenantHeader, out var tenantHeader)
            ? tenantHeader.ToString()
            : "no-tenant";
        var storefront = context.Request.Headers.TryGetValue(DomainResolutionMiddleware.StorefrontHeader, out var storefrontHeader)
            ? storefrontHeader.ToString()
            : "no-storefront";
        var isAuthPath = path.StartsWith("/api/identity/login", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/identity/register", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/identity/password-reset", StringComparison.OrdinalIgnoreCase);

        var kind = isAuthPath ? "auth" : "any";
        var key = kind + ":" + tenant + ":" + storefront + ":" + ip;
        return new Decision(key, kind, isAuthPath ? options.AuthPermitLimit : options.AnyPermitLimit, Window);
    }

    /// <summary>
    /// The legacy per-instance limiter (the <c>InMemory</c> backend and the fail-open fallback). The permit
    /// limit is derived from the key's <c>auth:</c> / <c>any:</c> prefix so it matches <see cref="Resolve"/>.
    /// </summary>
    public static PartitionedRateLimiter<string> CreateInProcessLimiter(RateLimitOptions options) =>
        PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(
                key,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = key.StartsWith("auth:", StringComparison.Ordinal) ? options.AuthPermitLimit : options.AnyPermitLimit,
                    Window = Window,
                    QueueLimit = 0,
                }));
}
