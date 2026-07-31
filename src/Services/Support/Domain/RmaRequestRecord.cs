namespace ThreeCommerce.Support.Domain;

/// <summary>
/// Customer-facing record of a refund/return request, persisted synchronously when the customer
/// submits it (the RMA saga — <c>RmaState</c> — owns the lifecycle <em>state</em>, correlated by the
/// same id). Holds the per-line quantities so the storefront can (a) show the customer their request
/// with its live status and (b) subtract already-requested units from what's still refundable.
/// </summary>
public class RmaRequestRecord
{
    public Guid Id { get; init; } // == RMA saga CorrelationId
    public Guid OrderId { get; init; }
    public required string Email { get; init; }
    public required string Reason { get; init; }
    public long AmountMinor { get; init; }
    public required string Currency { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<RmaRequestLine> Lines { get; init; } = [];
}

public class RmaRequestLine
{
    public Guid Id { get; init; }
    public Guid RmaId { get; init; }
    public Guid ProductId { get; init; }
    public required string Title { get; init; }
    public int Quantity { get; init; }
    public long UnitPriceMinor { get; init; }
}
