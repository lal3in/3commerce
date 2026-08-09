using StackExchange.Redis;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Redis;

/// <summary>
/// Redis-backed <see cref="IDedupeStore"/> (ADR-0044). Keys are namespaced and TTL'd so they self-expire.
/// All operations are best-effort: any outage returns "not processed" / no-ops so the caller's durable
/// Postgres dedupe remains the exactly-once guarantee.
/// </summary>
public sealed class RedisDedupeStore(IRedisConnection redis) : IDedupeStore
{
    public async Task<bool> IsProcessedAsync(string key, CancellationToken ct)
    {
        var db = redis.Database;
        if (db is null)
        {
            RedisMetrics.RecordUnavailable("idempotency");
            return false;
        }

        try
        {
            return await db.KeyExistsAsync(Key(key));
        }
        catch (RedisException)
        {
            RedisMetrics.RecordUnavailable("idempotency");
            return false;
        }
    }

    public async Task MarkProcessedAsync(string key, TimeSpan ttl, CancellationToken ct)
    {
        var db = redis.Database;
        if (db is null)
        {
            return;
        }

        try
        {
            await db.StringSetAsync(Key(key), "1", ttl);
        }
        catch (RedisException)
        {
            // Best-effort: a failed mark just means the next duplicate falls through to the Postgres dedupe.
        }
    }

    private static RedisKey Key(string key) => "dedupe:" + key;
}
