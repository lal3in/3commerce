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
/// </summary>
public record RefundRequested(
    Guid RefundId,
    Guid OrderId,
    long AmountMinor,
    string Reason,
    string RequestedBy,
    string? ChargeReference = null);
