using ThreeCommerce.BuildingBlocks.Infrastructure.Redis;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0044 distributed rate limiting: the Redis store must enforce ONE shared window regardless of which
/// gateway instance counts the request — the regression the old in-process limiter could not prevent
/// (N replicas → N× the intended limit).
/// </summary>
[Trait("Category", "Integration")]
[Collection(RedisCollection.Name)]
public class RedisRateLimitStoreTests(RedisFixture fixture)
{
    // A store over a freshly connected instance (stands in for one gateway replica), guaranteed available.
    private async Task<RedisRateLimitStore> StoreAsync()
    {
        var connection = fixture.NewConnection();
        _ = connection.Multiplexer; // trigger the lazy connect
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (!connection.IsAvailable && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(connection.IsAvailable, "Redis connection never became available");
        return new RedisRateLimitStore(connection);
    }

    [Fact]
    public async Task Enforces_the_limit_within_a_window()
    {
        var store = await StoreAsync();
        var key = $"auth:t:s:ip:{Guid.NewGuid():N}";
        var window = TimeSpan.FromMinutes(1);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(RateLimitOutcome.Allowed, await store.TryAcquireAsync(key, 3, window, default));
        }

        Assert.Equal(RateLimitOutcome.Rejected, await store.TryAcquireAsync(key, 3, window, default));
    }

    [Fact]
    public async Task Two_instances_share_one_window_over_one_redis()
    {
        // Two independent connections = two gateway replicas hitting the same Redis.
        var instanceA = await StoreAsync();
        var instanceB = await StoreAsync();
        var key = $"auth:t:s:ip:{Guid.NewGuid():N}";
        var window = TimeSpan.FromMinutes(1);
        const int limit = 4;

        // Alternate instances; the COMBINED count is what matters — 4 allowed, then rejected on both.
        Assert.Equal(RateLimitOutcome.Allowed, await instanceA.TryAcquireAsync(key, limit, window, default));
        Assert.Equal(RateLimitOutcome.Allowed, await instanceB.TryAcquireAsync(key, limit, window, default));
        Assert.Equal(RateLimitOutcome.Allowed, await instanceA.TryAcquireAsync(key, limit, window, default));
        Assert.Equal(RateLimitOutcome.Allowed, await instanceB.TryAcquireAsync(key, limit, window, default));

        // 5th request on EITHER instance is over the shared limit.
        Assert.Equal(RateLimitOutcome.Rejected, await instanceB.TryAcquireAsync(key, limit, window, default));
        Assert.Equal(RateLimitOutcome.Rejected, await instanceA.TryAcquireAsync(key, limit, window, default));
    }

    [Fact]
    public async Task Window_resets_after_expiry()
    {
        var store = await StoreAsync();
        var key = $"any:t:s:ip:{Guid.NewGuid():N}";
        var window = TimeSpan.FromMilliseconds(600);

        Assert.Equal(RateLimitOutcome.Allowed, await store.TryAcquireAsync(key, 1, window, default));
        Assert.Equal(RateLimitOutcome.Rejected, await store.TryAcquireAsync(key, 1, window, default));

        await Task.Delay(900); // let the window TTL lapse
        Assert.Equal(RateLimitOutcome.Allowed, await store.TryAcquireAsync(key, 1, window, default));
    }

    [Fact]
    public async Task Reports_unavailable_when_not_configured()
    {
        // An unconfigured connection (no Redis) must surface Unavailable so the gateway applies its
        // fail-open / fail-closed policy — it must never throw or silently allow/deny.
        var store = new RedisRateLimitStore(new NoRedis());
        Assert.Equal(RateLimitOutcome.Unavailable, await store.TryAcquireAsync("k", 1, TimeSpan.FromMinutes(1), default));
    }

    private sealed class NoRedis : IRedisConnection
    {
        public bool IsConfigured => false;
        public bool IsAvailable => false;
        public StackExchange.Redis.IConnectionMultiplexer? Multiplexer => null;
        public StackExchange.Redis.IDatabase? Database => null;
    }
}
