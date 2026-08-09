using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.Redis;
using ThreeCommerce.BuildingBlocks.Infrastructure.Redis;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// A single Redis (Valkey-compatible) container shared by the Redis fast-path tests (ADR-0044). Builds
/// real <see cref="IRedisConnection"/> handles through the production AddRedis wiring so tests exercise the
/// same client path the services use. <see cref="NewConnection"/> mints an independent connection to stand
/// in for a separate service instance (proving cross-instance behaviour over one shared Redis).
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();
    private readonly List<IHost> _hosts = [];

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        ConnectionString = _redis.GetConnectionString();
    }

    /// <summary>An <see cref="IRedisConnection"/> from a fresh host — one per simulated service instance.</summary>
    public IRedisConnection NewConnection()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            [new KeyValuePair<string, string?>("ConnectionStrings:Redis", ConnectionString)]);
        builder.AddRedis();
        var host = builder.Build();
        _hosts.Add(host);
        return host.Services.GetRequiredService<IRedisConnection>();
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        await _redis.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "redis";
}
