namespace ThreeCommerce.Ordering.Domain;

public enum OrderStatus { Pending = 1, AwaitingPayment = 2, Confirmed = 3, Cancelled = 4 }

/// <summary>Per-line fulfillment source (ADR-0003) — Unassigned in v1, decided per item later.</summary>
public enum FulfillmentSource { Unassigned = 0, Dropship = 1, OwnWarehouse = 2 }

public class Order
{
    public Guid Id { get; init; }
    public Guid? UserId { get; set; }
    public required string Email { get; set; }
    public OrderStatus Status { get; set; }
    public long NetMinor { get; set; }
    public long TaxMinor { get; set; }
    public long ShippingMinor { get; set; }
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
    public required string Title { get; set; }
    public long UnitPriceMinor { get; init; }
    public int Quantity { get; init; }
    public FulfillmentSource FulfillmentSource { get; set; } = FulfillmentSource.Unassigned;
}
