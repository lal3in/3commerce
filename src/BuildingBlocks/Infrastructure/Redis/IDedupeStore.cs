namespace ThreeCommerce.BuildingBlocks.Infrastructure.Redis;

/// <summary>
/// A TTL'd "have I already processed this?" fast-path (ADR-0044), used to drop duplicate webhook
/// deliveries before they touch Postgres. It is a POSITIVE cache populated only AFTER the durable
/// Postgres write commits, so it can never cause a lost event: a Redis outage or an evicted/expired key
/// simply makes <see cref="IsProcessedAsync"/> return false and the caller falls back to its durable
/// (Postgres) dedupe. Redis is an optimization, never the source of truth.
/// </summary>
public interface IDedupeStore
{
    /// <summary>True only if the key was previously marked processed and is still cached. False when Redis is unavailable.</summary>
    public Task<bool> IsProcessedAsync(string key, CancellationToken ct);

    /// <summary>Marks the key processed for <paramref name="ttl"/>. No-op when Redis is unavailable. Call only after the durable write commits.</summary>
    public Task MarkProcessedAsync(string key, TimeSpan ttl, CancellationToken ct);
}
