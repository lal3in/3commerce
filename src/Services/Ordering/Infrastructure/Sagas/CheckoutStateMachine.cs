using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;

namespace ThreeCommerce.Ordering.Infrastructure.Sagas;

/// <summary>
/// Orchestrates checkout after the intent exists (ADR-0007). CartSubmitted starts it in
/// AwaitingPayment with a 30-minute cancel timeout; the terminal transitions publish
/// OrderConfirmed / OrderCancelled (consumed by the order-status updater, Notifications,
/// and Fulfillment). Out-of-order webhooks are tolerated by the state guards.
/// </summary>
public sealed class CheckoutStateMachine : MassTransitStateMachine<CheckoutState>
{
    public State AwaitingPayment { get; private set; } = null!;
    public State Confirmed { get; private set; } = null!;
    public State Cancelled { get; private set; } = null!;

    public Event<CartSubmitted> CartSubmitted { get; private set; } = null!;
    public Event<PaymentSucceeded> PaymentSucceeded { get; private set; } = null!;
    public Event<PaymentFailed> PaymentFailed { get; private set; } = null!;
    public Schedule<CheckoutState, CheckoutExpired> ExpiryTimeout { get; private set; } = null!;

    public CheckoutStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => CartSubmitted, e => e.CorrelateById(c => c.Message.OrderId));
        Event(() => PaymentSucceeded, e => e.CorrelateById(c => c.Message.OrderId));
        Event(() => PaymentFailed, e => e.CorrelateById(c => c.Message.OrderId));

        Schedule(() => ExpiryTimeout, x => x.TimeoutTokenId, s =>
        {
            s.Delay = TimeSpan.FromMinutes(30);
            s.Received = e => e.CorrelateById(c => c.Message.OrderId);
        });

        Initially(
            When(CartSubmitted)
                .Then(c =>
                {
                    c.Saga.PaymentIntentId = c.Message.PaymentIntentId;
                    c.Saga.AmountMinor = c.Message.AmountMinor;
                    c.Saga.Email = c.Message.Email;
                    c.Saga.Currency = c.Message.Currency;
                    c.Saga.CreatedAt = DateTimeOffset.UtcNow;
                })
                .Schedule(ExpiryTimeout, c => new CheckoutExpired(c.Saga.CorrelationId))
                .TransitionTo(AwaitingPayment));

        During(AwaitingPayment,
            When(PaymentSucceeded)
                .Unschedule(ExpiryTimeout)
                // Signal payment settled; the Order aggregate owner publishes the rich
                // OrderConfirmed (with lines). Records have no default ctor → publish the
                // constructed instance directly, never Init<T>.
                .Publish(c => new CheckoutCompleted(c.Saga.CorrelationId))
                .TransitionTo(Confirmed)
                .Finalize(),
            When(PaymentFailed)
                .Unschedule(ExpiryTimeout)
                .Publish(c => new OrderCancelled(c.Saga.CorrelationId, c.Message.Reason))
                .TransitionTo(Cancelled)
                .Finalize(),
            When(ExpiryTimeout.Received)
                .Publish(c => new OrderCancelled(c.Saga.CorrelationId, "checkout timed out"))
                .TransitionTo(Cancelled)
                .Finalize());

        SetCompletedWhenFinalized();
    }
}

/// <summary>Internal saga timeout token.</summary>
public record CheckoutExpired(Guid OrderId);
