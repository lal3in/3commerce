namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

// FullyRefunded is set when this refund brings the order's captured payment to fully refunded, so
// consumers (e.g. Ordering) can move the order to Refunded only on a full — not partial — refund.
public record RefundCompleted(Guid RefundId, Guid OrderId, long AmountMinor, bool FullyRefunded = false);
