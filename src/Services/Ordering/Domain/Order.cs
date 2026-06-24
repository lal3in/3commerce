using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Ordering.Domain;

public enum OrderStatus { Pending = 1, AwaitingPayment = 2, Confirmed = 3, Cancelled = 4 }

public class Order
{
    public Guid Id { get; init; }
    public long PublicOrderNumber { get; set; }

    /// <summary>The owning tenant (carried from the checkout attempt) — Fulfillment scopes inventory by it.</summary>
    public Guid TenantId { get; set; }
    public Guid? StorefrontId { get; set; }
    public Guid? UserId { get; set; }
    public required string Email { get; set; }
    public OrderStatus Status { get; set; }
    public long NetMinor { get; set; }
    public long TaxMinor { get; set; }
    public long ShippingMinor { get; set; }
    public long DiscountMinor { get; set; }
    public long GrossMinor { get; set; }
    public required string Currency { get; set; }
    public string? PaymentIntentId { get; set; }
    public required string ShipName { get; set; }
    public required string ShipLine1 { get; set; }
    public required string ShipCity { get; set; }
    public required string ShipPostcode { get; set; }
    public required string ShipCountry { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<OrderLine> Lines { get; init; } = [];
}

public class OrderLine
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public Guid ProductId { get; init; }
    public Guid? VariantId { get; init; }
    public string? VariantSku { get; set; }
    public required string Title { get; set; }
    public long UnitPriceMinor { get; init; }
    public long DiscountMinor { get; init; }
    public int Quantity { get; init; }

    /// <summary>How this line is sourced/delivered (ADR-0028, shared vocabulary).</summary>
    public FulfilmentType FulfilmentType { get; set; } = FulfilmentType.Unassigned;

    /// <summary>The supplier fulfilling this line (from its resolved offer) — drives dropship routing.</summary>
    public Guid? SupplierId { get; set; }

    /// <summary>How this line is charged. One-time today; recurring/metered in Phase 7.</summary>
    public BillingMode BillingMode { get; set; } = BillingMode.OneTime;
}
