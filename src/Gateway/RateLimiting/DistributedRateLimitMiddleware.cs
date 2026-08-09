using System.Threading.RateLimiting;
using ThreeCommerce.BuildingBlocks.Infrastructure.Redis;

namespace ThreeCommerce.Gateway.RateLimiting;

/// <summary>
/// Enforces the gateway rate limit (ADR-0044). With <c>Backend=Redis</c> the limit is shared across all
/// gateway replicas via <see cref="IRateLimitStore"/>; with <c>Backend=InMemory</c> it uses the legacy
/// per-instance limiter. On a Redis outage the behaviour is the configured toggle: <c>FailOpen</c> falls
/// back to the in-process limiter (don't lock users out over a cache blip); <c>FailClosed</c> rejects with
/// 429 until Redis recovers. Every decision is metered on <c>3commerce.redis</c>.
/// </summary>
public sealed class DistributedRateLimitMiddleware(
    RequestDelegate next,
    IRateLimitStore store,
    RateLimitOptions options,
    PartitionedRateLimiter<string> inProcessLimiter,
    ILogger<DistributedRateLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var decision = RateLimitPolicy.Resolve(context);

        var allowed = options.Backend == RateLimitBackend.Redis
            ? await AllowedViaRedisAsync(decision, context.RequestAborted)
            : await AllowedInProcessAsync(decision.Key);

        if (!allowed)
        {
            RedisMetrics.RecordRateLimitDecision("reject", decision.Kind);
            logger.LogInformation("Rate limit rejected: partition={Kind} key={Key}", decision.Kind, decision.Key);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        RedisMetrics.RecordRateLimitDecision("allow", decision.Kind);
        await next(context);
    }

    private async Task<bool> AllowedViaRedisAsync(RateLimitPolicy.Decision d, CancellationToken ct)
    {
        var outcome = await store.TryAcquireAsync(d.Key, d.PermitLimit, d.Window, ct);
        switch (outcome)
        {
            case RateLimitOutcome.Allowed:
                return true;
            case RateLimitOutcome.Rejected:
                return false;
            default: // Unavailable — apply the configured outage policy
                RedisMetrics.RecordUnavailable("ratelimit");
                if (options.OnRedisOutage == RateLimitOutageMode.FailClosed)
                {
                    logger.LogWarning("Redis unavailable; rate limiting fail-closed (429) for {Key}", d.Key);
                    return false;
                }

                logger.LogWarning("Redis unavailable; rate limiting fail-open to in-process limiter for {Key}", d.Key);
                return await AllowedInProcessAsync(d.Key);
        }
    }

    private async Task<bool> AllowedInProcessAsync(string key)
    {
        using var lease = await inProcessLimiter.AcquireAsync(key, 1);
        return lease.IsAcquired;
    }
}
