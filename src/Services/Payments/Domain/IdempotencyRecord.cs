namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// Stores the response of a money-mutating request keyed by its Idempotency-Key, so a
/// replay returns the original result instead of acting twice (NFR-3, api.md §7).
/// </summary>
public class IdempotencyRecord
{
    public required string Key { get; init; }
    public required string RequestHash { get; init; }
    public required string ResponseJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
