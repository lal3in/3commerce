namespace ThreeCommerce.BuildingBlocks.Contracts.Ordering;

/// <summary>Payment captured. Consumed by Ordering (status), Notifications (email), Fulfillment (Phase 4).</summary>
public record OrderConfirmed(Guid OrderId, string Email, long AmountMinor, string Currency);
