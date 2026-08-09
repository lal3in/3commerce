namespace ThreeCommerce.BuildingBlocks.Infrastructure.Redis;

/// <summary>
/// A shared, cross-instance fixed-window rate-limit counter (ADR-0044). Backed by Redis so every gateway
/// replica enforces one combined limit instead of the previous in-process limiter that gave each replica
/// its own window (effective limit ~N× intended). Implementations must be atomic (a single round-trip
/// increment + expiry) and must surface unavailability via the return value so the caller can apply its
/// configured fail-open / fail-closed policy.
/// </summary>
public interface IRateLimitStore
{
    /// <summary>
    /// Atomically counts one request in the current window for <paramref name="partitionKey"/> and reports
    /// whether it is within <paramref name="permitLimit"/>. Returns <c>Unavailable</c> when Redis cannot be
    /// reached (the caller then applies its outage policy) — never throws for an outage.
    /// </summary>
    public Task<RateLimitOutcome> TryAcquireAsync(string partitionKey, int permitLimit, TimeSpan window, CancellationToken ct);
}

/// <summary>Result of a rate-limit check. <c>Unavailable</c> means Redis was unreachable, not "over limit".</summary>
public enum RateLimitOutcome
{
    Allowed,
    Rejected,
    Unavailable,
}
