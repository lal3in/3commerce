using ThreeCommerce.Fulfillment.Domain.Carriers;

namespace ThreeCommerce.Fulfillment.Domain.Dropship;

public sealed record SupplierOrderItem(Guid ProductId, Guid? VariantId, string? SupplierSku, int Quantity);

public sealed record SupplierOrderRequest(
    Guid OrderId, Guid SupplierId, string Email, ShipAddress Destination, IReadOnlyList<SupplierOrderItem> Items);

public sealed record SupplierOrderResult(
    bool Accepted, string? ExternalReference, string? TrackingNumber, string? Carrier, string? FailureReason);

/// <summary>
/// Forwards a dropship order to a supplier (mt4_4b). One implementation per integration; real
/// suppliers need per-source credentials and onboarding, so the Fake ships first.
/// </summary>
public interface ISupplierOrderProvider
{
    public string Key { get; }

    public Task<SupplierOrderResult> SubmitAsync(SupplierOrderRequest request, CancellationToken ct);
}

public enum SupplierOrderStatus { Requested = 1, Accepted = 2, TrackingReceived = 3, Failed = 4 }

/// <summary>
/// A dropship order forwarded to a supplier (mt4_4b): SupplierOrderRequested → Accepted →
/// TrackingReceived (or Failed). Pure dropship moves no internal inventory — it lives here.
/// </summary>
public sealed class SupplierOrder
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid OrderId { get; init; }
    public Guid SupplierId { get; init; }
    public SupplierOrderStatus Status { get; private set; } = SupplierOrderStatus.Requested;
    public string? ExternalReference { get; private set; }
    public string? TrackingNumber { get; private set; }
    public string? Carrier { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private SupplierOrder() { }

    public static SupplierOrder Request(Guid tenantId, Guid orderId, Guid supplierId, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || orderId == Guid.Empty || supplierId == Guid.Empty)
        {
            throw new FulfillmentRuleException("Supplier order tenant, order, and supplier are required.");
        }

        return new SupplierOrder
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrderId = orderId,
            SupplierId = supplierId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Apply(SupplierOrderResult result, DateTimeOffset now)
    {
        if (!result.Accepted)
        {
            Status = SupplierOrderStatus.Failed;
            FailureReason = result.FailureReason ?? "Supplier rejected the order.";
            UpdatedAt = now;
            return;
        }

        ExternalReference = result.ExternalReference;
        if (!string.IsNullOrWhiteSpace(result.TrackingNumber))
        {
            TrackingNumber = result.TrackingNumber;
            Carrier = result.Carrier;
            Status = SupplierOrderStatus.TrackingReceived;
        }
        else
        {
            Status = SupplierOrderStatus.Accepted;
        }

        UpdatedAt = now;
    }
}

public enum SupplierStockStatus { Available = 1, OutOfStock = 2, Discontinued = 3, Unknown = 4 }

/// <summary>
/// Supplier-fed availability for a dropship product (mt4_4b). Dropship has no internal inventory;
/// sellability comes from this feed. Suppliers can update it (not dispatch/tracking) in v1.
/// </summary>
public sealed class SupplierAvailability
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid SupplierId { get; init; }
    public Guid ProductId { get; init; }
    public Guid? VariantId { get; init; }
    public string? SupplierSku { get; private set; }
    public SupplierStockStatus Status { get; private set; } = SupplierStockStatus.Unknown;
    public int? ExternalQuantity { get; private set; }
    public DateTimeOffset LastCheckedAt { get; private set; }

    private SupplierAvailability() { }

    public static SupplierAvailability Create(
        Guid tenantId, Guid supplierId, Guid productId, Guid? variantId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SupplierId = supplierId,
            ProductId = productId,
            VariantId = variantId,
            LastCheckedAt = now,
        };

    public void Update(SupplierStockStatus status, int? externalQuantity, string? supplierSku, DateTimeOffset now)
    {
        Status = status;
        ExternalQuantity = externalQuantity;
        SupplierSku = string.IsNullOrWhiteSpace(supplierSku) ? SupplierSku : supplierSku.Trim();
        LastCheckedAt = now;
    }

    public bool IsSellable => Status == SupplierStockStatus.Available;
}
