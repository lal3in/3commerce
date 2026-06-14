namespace ThreeCommerce.Fulfillment.Domain;

public enum ShipmentStatus { Created = 1, Dispatched = 2 }

/// <summary>One shipment per (order, fulfillment source) — lines are grouped by source (ADR-0003).</summary>
public class Shipment
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public required string FulfillmentSource { get; init; }
    public ShipmentStatus Status { get; set; }
    public string? Carrier { get; set; }
    public string? TrackingNumber { get; set; }
    public string? Email { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<ShipmentLine> Lines { get; init; } = [];
}

public class ShipmentLine
{
    public Guid Id { get; init; }
    public Guid ShipmentId { get; init; }
    public Guid ProductId { get; init; }
    public required string Title { get; init; }
    public int Quantity { get; init; }
}
