using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ThreeCommerce.BuildingBlocks.Infrastructure.Redis;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Foundation guard for ADR-0044: when no <c>ConnectionStrings:Redis</c> is configured, AddRedis must
/// register a safe no-op connection so every fast-path caller falls back to Postgres / in-process
/// behaviour (local bare-runs and CI keep working without a Redis). Hermetic — no container, no network.
/// </summary>
[Trait("Category", "Unit")]
public class RedisConnectionTests
{
    private static IRedisConnection Resolve(params (string Key, string? Value)[] settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        builder.AddRedis();
        return builder.Build().Services.GetRequiredService<IRedisConnection>();
    }

    [Fact]
    public void Unconfigured_Redis_degrades_to_a_safe_no_op()
    {
        var redis = Resolve();

        Assert.False(redis.IsConfigured);
        Assert.False(redis.IsAvailable);
        Assert.Null(redis.Multiplexer);
        Assert.Null(redis.Database); // callers null-check and fall back — never throws
    }

    [Fact]
    public void Empty_connection_string_is_treated_as_unconfigured()
    {
        var redis = Resolve(("ConnectionStrings:Redis", ""));

        Assert.False(redis.IsConfigured);
        Assert.Null(redis.Database);
    }

    [Fact]
    public void A_connection_string_marks_it_configured()
    {
        // AbortOnConnectFail=false means this never blocks/throws even though nothing is listening;
        // it stays unavailable (Database null) until a real Valkey is reachable — the fallback contract.
        var redis = Resolve(("ConnectionStrings:Redis", "localhost:6399,connectTimeout=200,abortConnect=false"));

        Assert.True(redis.IsConfigured);
        Assert.False(redis.IsAvailable);
        Assert.Null(redis.Database);
    }
}
