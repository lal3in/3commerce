namespace ThreeCommerce.BuildingBlocks.Contracts.Ping;

/// <summary>
/// Phase-1 spine contract: proves outbox publish → broker → inbox-idempotent consume.
/// Kept permanently as the smoke-test flow. Contracts version additively only.
/// </summary>
public record PingRequested(Guid PingId, DateTimeOffset RequestedAt);
