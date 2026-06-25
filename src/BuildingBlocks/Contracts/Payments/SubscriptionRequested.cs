using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// A confirmed order has a recurring (subscription) line (Phase 7 / mt7_3). Payments sets up a
/// subscription so future periods can be charged behind IPaymentProvider. The first period is paid
/// as part of the order at checkout; this arranges the recurring renewal.
/// </summary>
public record SubscriptionRequested(
    Guid TenantId, Guid OrderId, string CustomerEmail, Guid ProductId, Guid? VariantId,
    BillingPeriod BillingPeriod, long PriceMinor, string Currency);
