using MassTransit;

namespace ThreeCommerce.Support.Infrastructure.Sagas;

/// <summary>RMA saga instance (correlated by RmaId). Also the admin read model.</summary>
public class RmaState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public required string CurrentState { get; set; }
    public Guid OrderId { get; set; }
    public string? Email { get; set; }
    public long AmountMinor { get; set; }
    public string? Reason { get; set; }
    public Guid RefundId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Why Payments could not execute the refund, when the RMA ended in RefundFailed — the
    /// reason an operator needs to decide whether to retry with a corrected amount (rma_disc).</summary>
    public string? RefundFailureReason { get; set; }

    /// <summary>Set when the operator marks the return received — lets the admin queue offer the
    /// restock/storage disposition step afterwards, regardless of the refund state.</summary>
    public DateTimeOffset? ReturnReceivedAt { get; set; }
}
