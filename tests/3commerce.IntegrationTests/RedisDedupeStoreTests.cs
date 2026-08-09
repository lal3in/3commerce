using ThreeCommerce.BuildingBlocks.Infrastructure.Redis;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0044 webhook dedupe fast-path: the dedupe store is a TTL'd positive cache. It must only report
/// "processed" for keys explicitly marked after a durable commit, self-expire, and degrade to
/// "not processed" when Redis is unavailable so the caller falls back to its Postgres dedupe.
/// </summary>
[Trait("Category", "Integration")]
[Collection(RedisCollection.Name)]
public class RedisDedupeStoreTests(RedisFixture fixture)
{
    private async Task<RedisDedupeStore> StoreAsync()
    {
        var connection = fixture.NewConnection();
        _ = connection.Multiplexer;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (!connection.IsAvailable && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(connection.IsAvailable, "Redis connection never became available");
        return new RedisDedupeStore(connection);
    }

    [Fact]
    public async Task Unmarked_key_is_not_processed_then_marked_key_is()
    {
        var store = await StoreAsync();
        var key = $"webhook:evt_{Guid.NewGuid():N}";

        Assert.False(await store.IsProcessedAsync(key, default)); // first delivery — must process
        await store.MarkProcessedAsync(key, TimeSpan.FromMinutes(5), default);
        Assert.True(await store.IsProcessedAsync(key, default));  // duplicate — dropped by the fast-path
    }

    [Fact]
    public async Task Mark_expires_after_ttl()
    {
        var store = await StoreAsync();
        var key = $"webhook:evt_{Guid.NewGuid():N}";

        await store.MarkProcessedAsync(key, TimeSpan.FromMilliseconds(500), default);
        Assert.True(await store.IsProcessedAsync(key, default));

        await Task.Delay(800);
        Assert.False(await store.IsProcessedAsync(key, default)); // Postgres backstops beyond the TTL
    }

    [Fact]
    public async Task Unavailable_redis_reports_not_processed_and_never_throws()
    {
        var store = new RedisDedupeStore(new NoRedis());
        Assert.False(await store.IsProcessedAsync("webhook:x", default));
        await store.MarkProcessedAsync("webhook:x", TimeSpan.FromMinutes(5), default); // no-op, no throw
    }

    private sealed class NoRedis : IRedisConnection
    {
        public bool IsConfigured => false;
        public bool IsAvailable => false;
        public StackExchange.Redis.IConnectionMultiplexer? Multiplexer => null;
        public StackExchange.Redis.IDatabase? Database => null;
    }
}
