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

    /// <summary>
    /// Runs <paramref name="onConnected"/> with the shared multiplexer once it exists (immediately when it already
    /// does). Used to attach the opt-in StackExchange.Redis span instrumentation without forcing an eager connect.
    /// No-op for a Redis-less process.
    /// </summary>
    public static void WhenConnected(this IRedisConnection connection, Action<IConnectionMultiplexer> onConnected)
    {
        if (connection is RedisConnection shared)
        {
            shared.WhenConnected(onConnected);
        }
    }

    private sealed class RedisConnection : IRedisConnection, IDisposable
    {
        private readonly Lazy<IConnectionMultiplexer?> _multiplexer;
        private readonly List<Action<IConnectionMultiplexer>> _onConnected = [];
        private readonly Lock _gate = new();
        private IConnectionMultiplexer? _connected;

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
                    var multiplexer = ConnectionMultiplexer.Connect(options);
                    Action<IConnectionMultiplexer>[] callbacks;
                    lock (_gate)
                    {
                        _connected = multiplexer;
                        callbacks = [.. _onConnected];
                        _onConnected.Clear();
                    }

                    foreach (var callback in callbacks)
                    {
                        callback(multiplexer);
                    }

                    return multiplexer;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Redis connect failed at startup; running without the Redis fast-path until it recovers.");
                    return null;
                }
            });
        }

        public bool IsConfigured { get; }

        public void WhenConnected(Action<IConnectionMultiplexer> onConnected)
        {
            if (!IsConfigured)
            {
                return;
            }

            IConnectionMultiplexer? connected;
            lock (_gate)
            {
                connected = _connected;
                if (connected is null)
                {
                    _onConnected.Add(onConnected);
                    return;
                }
            }

            onConnected(connected);
        }

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
