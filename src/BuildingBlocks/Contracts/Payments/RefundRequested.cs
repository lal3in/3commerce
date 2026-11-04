namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// The single refund entry point (ADR-0014). Published by the admin refund endpoint
/// now and by the Phase-4 RMA saga later — kept generic so both reuse one path.
/// <para>
/// <see cref="ChargeReference"/> (appended, renewal_usage_refund_path): null refunds the order's checkout payment, as
/// before; a renewal / usage charge's reference (<c>renew-{subscriptionId}-{sequence}</c>, <c>overage-…</c>,
/// <c>autoload-…</c>) refunds that charge instead. <see cref="OrderId"/> may then be <see cref="Guid.Empty"/> (a usage
/// charge has no order); when it is set it must be the charge's order.
/// </para>
/// <para>
/// CREDIT-NOTE ATTRIBUTION (ADR-0063 § 6.2, P5) is APPENDED after it, all defaulted. <see cref="Lines"/> says which
/// order lines the money is for (the Support RMA path sets them); null — an admin amount-only refund, or a request
/// published before P5 — means an amount-only credit spread proportionally over the original invoice. When
/// <see cref="Lines"/> is set, <c>Σ Lines.AmountMinor + (ShippingMinor ?? 0) == AmountMinor</c>. Payments' ledger
/// reversal does not read them (tax and shipping stay pro-rated on the amount, ADR-0014); it carries them onto
/// <see cref="RefundCompleted"/>.
/// </para>
/// </summary>
/// <param name="Lines">The order lines refunded, or null for an amount-only refund.</param>
/// <param name="ShippingMinor">The part of the amount that refunds shipping; null when not attributed.</param>
/// <param name="Kind">Why the money goes back; null when not stated (a pre-P5 request).</param>
public record RefundRequested(
    Guid RefundId,
    Guid OrderId,
    long AmountMinor,
    string Reason,
    string RequestedBy,
    string? ChargeReference = null,
    IReadOnlyList<RefundLine>? Lines = null,
    long? ShippingMinor = null,
    RefundKind? Kind = null);

/// <summary>
/// One order line a refund gives money back for (ADR-0063 § 6.2). <paramref name="LineId"/> is the order line's id as
/// <c>OrderLineInfo.LineId</c> published it; <paramref name="AmountMinor"/> is the money refunded for those units — their
/// DISCOUNTED value (ADR-0055), possibly reduced by the order's refundable-gross cap.
/// </summary>
public record RefundLine(Guid LineId, int Quantity, long AmountMinor);

/// <summary>Why a refund gives money back — the credit-note reason (ADR-0063 § 6.2). Crosses the wire as a number; append only.</summary>
public enum RefundKind
{
    /// <summary>Goods (or units) returned / refunded line by line — an RMA.</summary>
    Return = 1,

    /// <summary>A goodwill or price-reduction refund of an amount, not of particular lines.</summary>
    PriceReduction = 2,

    /// <summary>Shipping only.</summary>
    Shipping = 3,

    /// <summary>The order is cancelled before it was supplied.</summary>
    Cancellation = 4,
}
