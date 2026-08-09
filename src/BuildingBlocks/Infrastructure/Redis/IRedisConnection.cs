using StackExchange.Redis;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Redis;

/// <summary>
/// A resilient handle to the self-hosted Valkey/Redis (ADR-0044). Redis is a cache / fast-path, never a
/// source of truth, so every caller must tolerate it being unavailable: when <see cref="IsAvailable"/> is
/// false (not configured, or the multiplexer is currently disconnected) callers fall back to Postgres /
/// in-process behaviour. The underlying <see cref="IConnectionMultiplexer"/> reconnects on its own
/// (AbortOnConnectFail=false), so a transient outage self-heals without restarting the service.
/// </summary>
public interface IRedisConnection
{
    /// <summary>True when a connection string was configured (a Redis backend is intended for this process).</summary>
    public bool IsConfigured { get; }

    /// <summary>True when configured AND the multiplexer currently reports a live connection.</summary>
    public bool IsAvailable { get; }

    /// <summary>The shared multiplexer, or null when no connection string is configured.</summary>
    public IConnectionMultiplexer? Multiplexer { get; }

    /// <summary>The default database, or null when Redis is not configured/available. Callers fall back on null.</summary>
    public IDatabase? Database { get; }
}
