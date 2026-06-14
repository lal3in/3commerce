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
}
