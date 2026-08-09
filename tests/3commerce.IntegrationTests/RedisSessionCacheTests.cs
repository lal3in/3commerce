using Microsoft.Extensions.Options;
using ThreeCommerce.BuildingBlocks.Infrastructure.Redis;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Infrastructure.Sessions;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0044 session introspection cache: correctness of the cache-aside + invalidation contract that the
/// gateway's auth depends on. A cached session must be evicted on logout (token) and on credential
/// reset / ClaimsVersion bump (whole user), share one view across instances, be a no-op when disabled,
/// and never throw when Redis is unavailable.
/// </summary>
[Trait("Category", "Integration")]
[Collection(RedisCollection.Name)]
public class RedisSessionCacheTests(RedisFixture fixture)
{
    private static SessionInfo Sample(Guid userId) =>
        new(Guid.NewGuid(), userId, Guid.NewGuid(), "member", $"{userId:N}@example.test", DateTimeOffset.UtcNow.AddHours(1));

    private async Task<RedisSessionCache> CacheAsync(bool enabled = true)
    {
        var connection = fixture.NewConnection();
        _ = connection.Multiplexer;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (!connection.IsAvailable && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(connection.IsAvailable, "Redis connection never became available");
        return new RedisSessionCache(connection, Options.Create(new SessionCacheOptions { Enabled = enabled }));
    }

    [Fact]
    public async Task Set_then_get_round_trips_when_enabled()
    {
        var cache = await CacheAsync();
        var hash = $"h_{Guid.NewGuid():N}";
        var info = Sample(Guid.NewGuid());

        Assert.Null(await cache.GetAsync(hash, default)); // cold miss
        await cache.SetAsync(hash, info, default);

        var got = await cache.GetAsync(hash, default);
        Assert.NotNull(got);
        Assert.Equal(info.UserId, got!.UserId);
        Assert.Equal(info.Email, got.Email);
        Assert.Equal(info.Role, got.Role);
    }

    [Fact]
    public async Task Disabled_cache_never_stores()
    {
        var cache = await CacheAsync(enabled: false);
        var hash = $"h_{Guid.NewGuid():N}";

        await cache.SetAsync(hash, Sample(Guid.NewGuid()), default);
        Assert.Null(await cache.GetAsync(hash, default)); // always a miss → introspection always hits Postgres
    }

    [Fact]
    public async Task Logout_invalidates_the_single_session()
    {
        var cache = await CacheAsync();
        var userId = Guid.NewGuid();
        var hash = $"h_{Guid.NewGuid():N}";
        await cache.SetAsync(hash, Sample(userId), default);
        Assert.NotNull(await cache.GetAsync(hash, default));

        await cache.InvalidateTokenAsync(hash, userId, default);
        Assert.Null(await cache.GetAsync(hash, default));
    }

    [Fact]
    public async Task Reset_or_claims_bump_invalidates_all_of_a_users_sessions()
    {
        var cache = await CacheAsync();
        var userId = Guid.NewGuid();
        var h1 = $"h_{Guid.NewGuid():N}";
        var h2 = $"h_{Guid.NewGuid():N}";
        await cache.SetAsync(h1, Sample(userId), default);
        await cache.SetAsync(h2, Sample(userId), default);

        await cache.InvalidateUserAsync(userId, default);

        Assert.Null(await cache.GetAsync(h1, default));
        Assert.Null(await cache.GetAsync(h2, default));
    }

    [Fact]
    public async Task Shared_across_instances_including_invalidation()
    {
        var instanceA = await CacheAsync();
        var instanceB = await CacheAsync();
        var userId = Guid.NewGuid();
        var hash = $"h_{Guid.NewGuid():N}";

        await instanceA.SetAsync(hash, Sample(userId), default);
        Assert.NotNull(await instanceB.GetAsync(hash, default)); // B sees what A cached (one shared Redis)

        await instanceB.InvalidateUserAsync(userId, default);
        Assert.Null(await instanceA.GetAsync(hash, default));    // A no longer serves the revoked session
    }

    [Fact]
    public async Task Unavailable_redis_is_a_safe_miss_and_never_throws()
    {
        var cache = new RedisSessionCache(new NoRedis(), Options.Create(new SessionCacheOptions { Enabled = true }));
        var userId = Guid.NewGuid();

        Assert.Null(await cache.GetAsync("h", default));
        await cache.SetAsync("h", Sample(userId), default);       // no-op, no throw
        await cache.InvalidateTokenAsync("h", userId, default);   // no-op, no throw
        await cache.InvalidateUserAsync(userId, default);         // no-op, no throw
    }

    private sealed class NoRedis : IRedisConnection
    {
        public bool IsConfigured => false;
        public bool IsAvailable => false;
        public StackExchange.Redis.IConnectionMultiplexer? Multiplexer => null;
        public StackExchange.Redis.IDatabase? Database => null;
    }
}
