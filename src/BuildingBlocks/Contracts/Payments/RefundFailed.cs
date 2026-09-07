namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// The negative outcome of the single refund execution path (ADR-0014), published whenever a
/// <see cref="RefundRequested"/> cannot be executed: no captured payment, an amount the remaining
/// balance cannot cover, or a provider decline.
/// <para>
/// It exists because those cases used to be a LOG LINE and a silent return, which left the requesting
/// RMA saga parked in RefundPending forever — no refund, no failure, nothing an operator could see.
/// Every non-success exit from the consumer now says so out loud, so the requester can reach a
/// terminal state and the shopper can be told.
/// </para>
/// </summary>
public record RefundFailed(Guid RefundId, Guid OrderId, long AmountMinor, RefundFailureReason Reason, string Detail);

/// <summary>Why a refund could not be executed. Crosses the bus as a NUMBER (platform invariant).</summary>
public enum RefundFailureReason
{
    /// <summary>The order has no captured payment to reverse.</summary>
    NoCapturedPayment = 1,

    /// <summary>The amount is non-positive, or exceeds what is left of the captured payment.</summary>
    AmountExceedsRemaining = 2,

    /// <summary>The payment provider declined the refund.</summary>
    ProviderDeclined = 3,
}
