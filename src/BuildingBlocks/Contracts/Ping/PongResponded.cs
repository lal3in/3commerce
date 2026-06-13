namespace ThreeCommerce.BuildingBlocks.Contracts.Ping;

public record PongResponded(Guid PingId, string RespondedBy);
