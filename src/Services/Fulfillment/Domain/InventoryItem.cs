namespace ThreeCommerce.Fulfillment.Domain;

/// <summary>
/// On-hand stock for a (product, variant) at one location. Reservations (QuantityReserved)
/// are driven by the checkout/payment saga in mt4_2; mt4_1 owns the on-hand feed.
/// </summary>
public sealed class InventoryItem
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid LocationId { get; init; }

    public Guid ProductId { get; init; }

    /// <summary>Null for a product with no variants (single-SKU).</summary>
    public Guid? VariantId { get; init; }

    public int QuantityOnHand { get; private set; }

    /// <summary>Hard reservations held against on-hand (set by mt4_2).</summary>
    public int QuantityReserved { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Sellable stock: on-hand net of reservations, never negative.</summary>
    public int Available => Math.Max(0, QuantityOnHand - QuantityReserved);

    private InventoryItem() { }

    public static InventoryItem Create(
        Guid tenantId, Guid locationId, Guid productId, Guid? variantId, int onHand, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || locationId == Guid.Empty || productId == Guid.Empty)
        {
            throw new FulfillmentRuleException("Inventory tenant, location, and product IDs are required.");
        }

        if (onHand < 0)
        {
            throw new FulfillmentRuleException("Stock on hand cannot be negative.");
        }

        return new InventoryItem
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            LocationId = locationId,
            ProductId = productId,
            VariantId = variantId,
            QuantityOnHand = onHand,
            UpdatedAt = now,
        };
    }

    /// <summary>Supplier/admin stock feed: set the absolute on-hand quantity.</summary>
    public void SetOnHand(int onHand, DateTimeOffset now)
    {
        if (onHand < 0)
        {
            throw new FulfillmentRuleException("Stock on hand cannot be negative.");
        }

        QuantityOnHand = onHand;
        UpdatedAt = now;
    }

    /// <summary>Hold stock against an in-flight order (mt4_2). Never reserves beyond what is available.</summary>
    public void Reserve(int quantity, DateTimeOffset now)
    {
        if (quantity <= 0)
        {
            throw new FulfillmentRuleException("Reserve quantity must be positive.");
        }

        if (quantity > Available)
        {
            throw new FulfillmentRuleException("Insufficient available stock to reserve.");
        }

        QuantityReserved += quantity;
        UpdatedAt = now;
    }

    /// <summary>Release a hold (order cancelled before confirmation). Clamped so reserved never goes negative.</summary>
    public void Release(int quantity, DateTimeOffset now)
    {
        if (quantity <= 0)
        {
            throw new FulfillmentRuleException("Release quantity must be positive.");
        }

        QuantityReserved = Math.Max(0, QuantityReserved - quantity);
        UpdatedAt = now;
    }

    /// <summary>Convert a hold into a sale: consume on-hand and drop the matching reservation.</summary>
    public void ConfirmReservation(int quantity, DateTimeOffset now)
    {
        if (quantity <= 0)
        {
            throw new FulfillmentRuleException("Confirm quantity must be positive.");
        }

        QuantityReserved = Math.Max(0, QuantityReserved - quantity);
        QuantityOnHand = Math.Max(0, QuantityOnHand - quantity);
        UpdatedAt = now;
    }

    /// <summary>Relative correction (e.g. +25 received, -2 shrinkage). Cannot drive on-hand below reservations.</summary>
    public void Adjust(int delta, DateTimeOffset now)
    {
        var next = QuantityOnHand + delta;
        if (next < 0)
        {
            throw new FulfillmentRuleException("Adjustment would make on-hand negative.");
        }

        if (next < QuantityReserved)
        {
            throw new FulfillmentRuleException("Adjustment would drop on-hand below reserved stock.");
        }

        QuantityOnHand = next;
        UpdatedAt = now;
    }
}
