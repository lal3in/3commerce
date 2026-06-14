namespace ThreeCommerce.BuildingBlocks.Contracts.Support;

/// <summary>RMA lifecycle notifications (Requested/Approved/Denied/RefundIssued…) for emails.</summary>
public record RmaStateChanged(Guid RmaId, Guid OrderId, string Email, string State);
