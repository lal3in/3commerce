namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// The single refund entry point (ADR-0014). Published by the admin refund endpoint
/// now and by the Phase-4 RMA saga later — kept generic so both reuse one path.
/// </summary>
public record RefundRequested(
    Guid RefundId,
    Guid OrderId,
    long AmountMinor,
    string Reason,
    string RequestedBy);
