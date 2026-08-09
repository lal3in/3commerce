using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Redis;

/// <summary>
/// Registers the shared self-hosted Valkey/Redis connection (ADR-0044). Reads
/// <c>ConnectionStrings:Redis</c>; when it is empty the process runs Redis-less and every
/// <see cref="IRedisConnection"/> consumer transparently falls back to Postgres / in-process behaviour,
/// so local bare-runs (`dotnet run` without the infra compose) keep working unchanged.
/// </summary>
public static class RedisExtensions
{
    public static TBuilder AddRedis<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var connectionString = builder.Configuration.GetConnectionString("Redis");
        builder.Services.AddSingleton<IRedisConnection>(sp =>
            new RedisConnection(connectionString, sp.GetService<ILogger<RedisConnection>>()));
        return builder;
    }

    private sealed class RedisConnection : IRedisConnection, IDisposable
    {
        private readonly Lazy<IConnectionMultiplexer?> _multiplexer;

        public RedisConnection(string? connectionString, ILogger<RedisConnection>? logger)
        {
            IsConfigured = !string.IsNullOrWhiteSpace(connectionString);
            _multiplexer = new Lazy<IConnectionMultiplexer?>(() =>
            {
                if (!IsConfigured)
                {
                    return null;
                }

                // AbortOnConnectFail=false: Connect never throws on a cold/absent Redis — it returns a
                // multiplexer that keeps reconnecting in the background, so callers just see IsAvailable=false
                // until it heals (no service crash on a Redis outage).
                var options = ConfigurationOptions.Parse(connectionString!);
                options.AbortOnConnectFail = false;
                try
                {
                    return ConnectionMultiplexer.Connect(options);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Redis connect failed at startup; running without the Redis fast-path until it recovers.");
                    return null;
                }
            });
        }

        public bool IsConfigured { get; }

        public IConnectionMultiplexer? Multiplexer => _multiplexer.Value;

        public bool IsAvailable => Multiplexer is { IsConnected: true };

        public IDatabase? Database => IsAvailable ? Multiplexer!.GetDatabase() : null;

        public void Dispose()
        {
            if (_multiplexer.IsValueCreated)
            {
                _multiplexer.Value?.Dispose();
            }
        }
    }
}
