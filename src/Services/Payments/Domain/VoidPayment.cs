namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// A void payment record created when a dispute is lost (the merchant loses the case as the final
/// outcome — <c>charge.dispute.closed</c> with dispute status <c>lost</c>). The original payment moves to
/// <see cref="PaymentStatus.Chargeback"/> and this record captures the reversed money as a distinct, audit-
/// friendly artifact (the ledger reversal itself was already booked when the funds were withdrawn). One
/// record per original payment — creation is idempotent on <see cref="OriginalPaymentId"/>.
/// </summary>
public sealed class VoidPayment
{
    public Guid Id { get; init; }
    public Guid OriginalPaymentId { get; init; }
    public Guid OrderId { get; init; }
    public required string PaymentIntentId { get; init; }

    /// <summary>The provider dispute id (<c>dp_…</c>) that closed as lost.</summary>
    public string? ProviderDisputeId { get; init; }

    /// <summary>The voided (charged-back) gross, in the payment's currency minor units.</summary>
    public long AmountMinor { get; init; }
    public required string Currency { get; init; }
    public string Reason { get; init; } = "dispute_lost";
    public DateTimeOffset CreatedAt { get; init; }
}
