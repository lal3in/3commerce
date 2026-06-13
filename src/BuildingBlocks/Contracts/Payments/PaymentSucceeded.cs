namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

public record PaymentSucceeded(Guid OrderId, string PaymentIntentId, long AmountMinor);
