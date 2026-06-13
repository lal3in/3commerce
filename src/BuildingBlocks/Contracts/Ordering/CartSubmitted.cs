namespace ThreeCommerce.BuildingBlocks.Contracts.Ordering;

/// <summary>
/// Starts the checkout saga. By the time this is published the payment intent
/// already exists (the endpoint requested it synchronously) — the saga owns the
/// async remainder: waiting for payment success/failure/timeout.
/// </summary>
public record CartSubmitted(
    Guid OrderId,
    string PaymentIntentId,
    long AmountMinor,
    string Currency,
    string Email);
