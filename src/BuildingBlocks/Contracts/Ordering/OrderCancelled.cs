namespace ThreeCommerce.BuildingBlocks.Contracts.Ordering;

public record OrderCancelled(Guid OrderId, string Reason);
