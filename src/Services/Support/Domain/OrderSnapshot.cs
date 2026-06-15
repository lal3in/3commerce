namespace ThreeCommerce.Support.Domain;

/// <summary>
/// Local read copy of a confirmed order (lines + prices), fed by OrderConfirmed events
/// (ADR-0008). Lets Support compute refund amounts server-side from the order itself,
/// so the customer selects lines rather than sending a trusted amount (BL-8).
/// </summary>
public class OrderSnapshot
{
    public Guid OrderId { get; init; }
    public required string Email { get; init; }
    public long GrossMinor { get; init; }
    public required string Currency { get; init; }
    public List<OrderSnapshotLine> Lines { get; init; } = [];
}

public class OrderSnapshotLine
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public Guid ProductId { get; init; }
    public required string Title { get; init; }
    public long UnitPriceMinor { get; init; }
    public int Quantity { get; init; }
}
