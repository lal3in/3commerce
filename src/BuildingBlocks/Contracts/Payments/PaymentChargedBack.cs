namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// Published when a dispute closes as <b>lost</b> — the merchant lost the case as the final outcome
/// (<c>charge.dispute.closed</c>, status <c>lost</c>). The original payment has moved to
/// <c>PaymentStatus.Chargeback</c>, a void payment record was created, and the ledger reversal was already
/// booked when the funds were withdrawn. Terminal counterpart to <see cref="PaymentDisputed"/> (which fires
/// when a dispute merely opens). <see cref="AmountMinor"/> is the charged-back gross.
/// </summary>
public record PaymentChargedBack(Guid OrderId, string PaymentIntentId, long AmountMinor);
