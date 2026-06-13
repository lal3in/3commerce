namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

public record RefundCompleted(Guid RefundId, Guid OrderId, long AmountMinor);
