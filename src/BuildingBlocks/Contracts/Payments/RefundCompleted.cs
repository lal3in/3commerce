namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

// FullyRefunded is set when this refund brings the refunded payment to fully refunded, so consumers (e.g. Ordering)
// can move the order to Refunded only on a full — not partial — refund. ChargeReference (appended,
// renewal_usage_refund_path) is null for the order's checkout payment and names the renewal / usage charge otherwise —
// a charge's refund is not the order's, so Ordering leaves the order alone.
// Lines / ShippingMinor / Kind (APPENDED after ChargeReference, ADR-0063 P5) are copied from the RefundRequested this
// refund executed — the credit-note attribution; null on an amount-only refund and on an event published before P5
// (see RefundRequested).
public record RefundCompleted(
    Guid RefundId,
    Guid OrderId,
    long AmountMinor,
    bool FullyRefunded = false,
    string? ChargeReference = null,
    IReadOnlyList<RefundLine>? Lines = null,
    long? ShippingMinor = null,
    RefundKind? Kind = null);
