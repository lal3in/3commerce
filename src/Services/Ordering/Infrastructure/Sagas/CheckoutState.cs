using MassTransit;

namespace ThreeCommerce.Ordering.Infrastructure.Sagas;

/// <summary>Persisted checkout saga instance (correlated by OrderId).</summary>
public class CheckoutState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public required string CurrentState { get; set; }
    public string? PaymentIntentId { get; set; }
    public long AmountMinor { get; set; }
    public string? Email { get; set; }
    public string? Currency { get; set; }
    public Guid? TimeoutTokenId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
