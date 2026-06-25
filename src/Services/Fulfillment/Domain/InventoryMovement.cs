namespace ThreeCommerce.Fulfillment.Domain;

/// <summary>What caused a stock change (mt4_2 ledger). Append-only; seeds audit (mt6_1).</summary>
public enum InventoryMovementType
{
    PurchaseIn = 1,
    Adjustment = 2,
    OrderReserved = 3,
    OrderConfirmed = 4,
    OrderCancelled = 5,
    Returned = 6,
    Transfer = 7,
}

public enum InventoryReferenceType
{
    None = 0,
    Order = 1,
    Refund = 2,
    ManualAdjustment = 3,
    SupplierSync = 4,
}

/// <summary>
/// An append-only record of one stock change at a location (mt4_2). Movements are the audit
/// trail; the live counters live on <see cref="InventoryItem"/>. Quantity is the magnitude moved.
/// </summary>
public sealed class InventoryMovement
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid InventoryItemId { get; init; }
    public Guid LocationId { get; init; }
    public Guid ProductId { get; init; }
    public Guid? VariantId { get; init; }
    public InventoryMovementType Type { get; init; }
    public int Quantity { get; init; }
    public InventoryReferenceType ReferenceType { get; init; }
    public Guid? ReferenceId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public static InventoryMovement For(
        InventoryItem item, InventoryMovementType type, int quantity,
        InventoryReferenceType referenceType, Guid? referenceId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = item.TenantId,
            InventoryItemId = item.Id,
            LocationId = item.LocationId,
            ProductId = item.ProductId,
            VariantId = item.VariantId,
            Type = type,
            Quantity = quantity,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            CreatedAt = now,
        };
}
