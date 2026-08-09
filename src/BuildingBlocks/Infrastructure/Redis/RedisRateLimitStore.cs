using StackExchange.Redis;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Redis;

/// <summary>
/// Redis-backed fixed-window rate limiter (ADR-0044). One atomic Lua script per request does INCR + set
/// the window TTL on the first hit, so N gateway replicas share a single window. Keys are namespaced and
/// carry the window TTL, so they self-expire — no cleanup, no unbounded growth.
/// </summary>
public sealed class RedisRateLimitStore(IRedisConnection redis) : IRateLimitStore
{
    // KEYS[1] = counter key; ARGV[1] = window milliseconds. Returns the post-increment count.
    // PEXPIRE only on the first hit so the window is anchored to the first request, not extended by later ones.
    private const string IncrementScript =
        "local c = redis.call('INCR', KEYS[1]) " +
        "if c == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end " +
        "return c";

    public async Task<RateLimitOutcome> TryAcquireAsync(string partitionKey, int permitLimit, TimeSpan window, CancellationToken ct)
    {
        var db = redis.Database;
        if (db is null)
        {
            return RateLimitOutcome.Unavailable;
        }

        try
        {
            var key = (RedisKey)("ratelimit:" + partitionKey);
            var count = (long)await db.ScriptEvaluateAsync(
                IncrementScript,
                [key],
                [(RedisValue)(long)window.TotalMilliseconds]);
            return count <= permitLimit ? RateLimitOutcome.Allowed : RateLimitOutcome.Rejected;
        }
        catch (RedisException)
        {
            // Connection dropped mid-flight — treat as unavailable so the caller applies its outage policy.
            return RateLimitOutcome.Unavailable;
        }
    }
}
