namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

public record PaymentFailed(Guid OrderId, string PaymentIntentId, string Reason);
