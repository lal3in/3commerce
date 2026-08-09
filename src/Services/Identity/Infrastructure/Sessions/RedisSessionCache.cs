using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using ThreeCommerce.BuildingBlocks.Infrastructure.Redis;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.Identity.Infrastructure.Sessions;

/// <summary>
/// Redis-backed <see cref="ISessionCache"/> (ADR-0044). A cached entry (<c>sess:tok:{hash}</c>) is a
/// short-TTL copy of the introspection result; a per-user set (<c>sess:usr:{userId}</c>) tracks that
/// user's cached token hashes so a single event (reset / ClaimsVersion bump) can evict them all — the
/// invalidation paths that don't have the raw tokens. Every operation is best-effort: a Redis outage or a
/// missed invalidation only costs up to <see cref="EntryTtl"/> of staleness, because Postgres remains the
/// authoritative check on the next miss.
/// </summary>
public sealed class RedisSessionCache(IRedisConnection redis, IOptions<SessionCacheOptions> options) : ISessionCache
{
    // Short TTL is the security backstop: even if an invalidation is ever missed, a revoked/role-changed
    // session cannot be served from cache longer than this.
    private static readonly TimeSpan EntryTtl = TimeSpan.FromSeconds(60);
    // The user->hashes set must outlive individual entries so bulk eviction can find them; capped to the
    // session lifetime so it can't grow forever.
    private static readonly TimeSpan UserSetTtl = TimeSpan.FromDays(14);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly bool _enabled = options.Value.Enabled;

    private static RedisKey Tok(string hash) => "sess:tok:" + hash;
    private static RedisKey Usr(Guid userId) => "sess:usr:" + userId.ToString("N");

    public async Task<SessionInfo?> GetAsync(string tokenHash, CancellationToken ct)
    {
        if (!_enabled)
        {
            return null;
        }

        var db = redis.Database;
        if (db is null)
        {
            RedisMetrics.RecordUnavailable("session");
            RedisMetrics.RecordSessionCacheMiss();
            return null;
        }

        try
        {
            var value = await db.StringGetAsync(Tok(tokenHash));
            if (value.IsNullOrEmpty)
            {
                RedisMetrics.RecordSessionCacheMiss();
                return null;
            }

            RedisMetrics.RecordSessionCacheHit();
            return JsonSerializer.Deserialize<SessionInfo>((string)value!, Json);
        }
        catch (RedisException)
        {
            RedisMetrics.RecordUnavailable("session");
            RedisMetrics.RecordSessionCacheMiss();
            return null;
        }
    }

    public async Task SetAsync(string tokenHash, SessionInfo info, CancellationToken ct)
    {
        if (!_enabled)
        {
            return;
        }

        var db = redis.Database;
        if (db is null)
        {
            return;
        }

        try
        {
            await db.StringSetAsync(Tok(tokenHash), JsonSerializer.Serialize(info, Json), EntryTtl);
            await db.SetAddAsync(Usr(info.UserId), tokenHash);
            await db.KeyExpireAsync(Usr(info.UserId), UserSetTtl);
        }
        catch (RedisException)
        {
            // Best-effort: a failed prime just means the next introspection is a normal Postgres read.
        }
    }

    public async Task InvalidateTokenAsync(string tokenHash, Guid userId, CancellationToken ct)
    {
        if (!_enabled)
        {
            return;
        }

        var db = redis.Database;
        if (db is null)
        {
            return;
        }

        try
        {
            await db.KeyDeleteAsync(Tok(tokenHash));
            await db.SetRemoveAsync(Usr(userId), tokenHash);
        }
        catch (RedisException)
        {
            // A missed eviction is bounded by the entry TTL; Postgres is authoritative on the next miss.
        }
    }

    public async Task InvalidateUserAsync(Guid userId, CancellationToken ct)
    {
        if (!_enabled)
        {
            return;
        }

        var db = redis.Database;
        if (db is null)
        {
            return;
        }

        try
        {
            var hashes = await db.SetMembersAsync(Usr(userId));
            if (hashes.Length > 0)
            {
                await db.KeyDeleteAsync(hashes.Select(h => Tok(h!)).ToArray());
            }

            await db.KeyDeleteAsync(Usr(userId));
        }
        catch (RedisException)
        {
            // Bounded by the entry TTL; Postgres is authoritative on the next miss.
        }
    }
}
