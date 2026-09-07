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
    ShipToInfo ShipTo,
    IReadOnlyList<OrderLineInfo> Lines);

public record ShipToInfo(string Name, string Line1, string City, string Postcode, string Country, string? Region = null);

/// <summary>
/// One confirmed order line. <paramref name="UnitPriceMinor"/> is the LISTED per-unit price;
/// <paramref name="DiscountMinor"/> is this line's whole allocated share of the order's discount
/// (promotion + storefront-wide), so the line's real selling value is
/// <c>UnitPriceMinor × Quantity − DiscountMinor</c> — the basis a refund must be computed on (rma_disc).
/// The allocations across an order's lines sum to <c>Order.DiscountMinor</c> exactly.
/// <para>
/// APPENDED with a default (contracts version additively): a consumer replaying an event published
/// before this field existed reads 0, i.e. "no discount known" — the pre-change behaviour.
/// </para>
/// </summary>
public record OrderLineInfo(
    Guid ProductId, Guid? VariantId, Guid? SupplierId, string Title, int Quantity,
    FulfilmentType FulfilmentType, BillingMode BillingMode, long UnitPriceMinor,
    long DiscountMinor = 0);
