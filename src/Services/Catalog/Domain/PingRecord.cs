namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// Phase-1 spine entity: exists so the ping endpoint has a real row to commit
/// in the same transaction as the outbox message (outbox atomicity proof).
/// </summary>
public class PingRecord
{
    public Guid Id { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
}
