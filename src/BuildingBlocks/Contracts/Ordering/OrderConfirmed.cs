using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.BuildingBlocks.Contracts.Ordering;

/// <summary>
/// Rich domain event published by the Order aggregate owner (Ordering's status updater)
/// once payment is captured. Consumed by Notifications (email) and Fulfillment (shipments).
/// </summary>
public record OrderConfirmed(
    Guid OrderId,
    Guid TenantId,
    string Email,
    long AmountMinor,
    string Currency,
    IReadOnlyList<OrderLineInfo> Lines);

public record OrderLineInfo(
    Guid ProductId, Guid? VariantId, string Title, int Quantity,
    FulfilmentType FulfilmentType, BillingMode BillingMode, long UnitPriceMinor);
