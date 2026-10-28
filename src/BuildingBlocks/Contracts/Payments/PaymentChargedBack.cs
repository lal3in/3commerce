namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// Published when a dispute closes as <b>lost</b> — the merchant lost the case as the final outcome
/// (<c>charge.dispute.closed</c>, status <c>lost</c>). The original payment has moved to
/// <c>PaymentStatus.Chargeback</c>, a void payment record was created, and the ledger reversal was already
/// booked when the funds were withdrawn. Terminal counterpart to <see cref="PaymentDisputed"/> (which fires
/// when a dispute merely opens). <see cref="AmountMinor"/> is the charged-back gross.
/// <see cref="ChargeReference"/> (appended, renewal_usage_refund_path) is null for the order's checkout payment and
/// names the charged-back renewal / usage charge otherwise — not the order's, so Ordering leaves the order alone.
/// </summary>
public record PaymentChargedBack(Guid OrderId, string PaymentIntentId, long AmountMinor, string? ChargeReference = null);
