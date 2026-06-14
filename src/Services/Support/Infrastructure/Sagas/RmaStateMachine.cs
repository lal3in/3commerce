using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Support;

namespace ThreeCommerce.Support.Infrastructure.Sagas;

/// <summary>
/// RMA lifecycle (ADR-0018): Requested → Approved/Denied → (AwaitingReturn → ReturnReceived)
/// → RefundIssued. Approval publishes the single Phase-3 RefundRequested contract — Support
/// never touches Stripe or the ledger directly. RefundCompleted advances to RefundIssued.
/// </summary>
public sealed class RmaStateMachine : MassTransitStateMachine<RmaState>
{
    public State Requested { get; private set; } = null!;
    public State AwaitingReturn { get; private set; } = null!;
    public State RefundPending { get; private set; } = null!;
    public State Denied { get; private set; } = null!;
    public State RefundIssued { get; private set; } = null!;

    public Event<RmaRequested> RmaRequestedEvent { get; private set; } = null!;
    public Event<RmaApproved> RmaApprovedEvent { get; private set; } = null!;
    public Event<RmaDenied> RmaDeniedEvent { get; private set; } = null!;
    public Event<ReturnReceived> ReturnReceivedEvent { get; private set; } = null!;
    public Event<RefundCompleted> RefundCompletedEvent { get; private set; } = null!;

    public RmaStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => RmaRequestedEvent, e => e.CorrelateById(c => c.Message.RmaId));
        Event(() => RmaApprovedEvent, e => e.CorrelateById(c => c.Message.RmaId));
        Event(() => RmaDeniedEvent, e => e.CorrelateById(c => c.Message.RmaId));
        Event(() => ReturnReceivedEvent, e => e.CorrelateById(c => c.Message.RmaId));
        // RefundCompleted carries the RefundId we generated; match it back to the saga.
        Event(() => RefundCompletedEvent, e => e.CorrelateBy((saga, ctx) => saga.RefundId == ctx.Message.RefundId));

        Initially(
            When(RmaRequestedEvent)
                .Then(c =>
                {
                    c.Saga.OrderId = c.Message.OrderId;
                    c.Saga.Email = c.Message.Email;
                    c.Saga.AmountMinor = c.Message.AmountMinor;
                    c.Saga.Reason = c.Message.Reason;
                    c.Saga.CreatedAt = DateTimeOffset.UtcNow;
                })
                .Publish(c => new RmaStateChanged(c.Saga.CorrelationId, c.Saga.OrderId, c.Saga.Email!, "Requested"))
                .TransitionTo(Requested));

        During(Requested,
            When(RmaApprovedEvent)
                .IfElse(c => c.Message.RequireReturn,
                    requireReturn => requireReturn
                        .Publish(c => new RmaStateChanged(c.Saga.CorrelationId, c.Saga.OrderId, c.Saga.Email!, "AwaitingReturn"))
                        .TransitionTo(AwaitingReturn),
                    noReturn => noReturn
                        .Then(c => c.Saga.RefundId = Guid.CreateVersion7())
                        .Publish(c => new RefundRequested(c.Saga.RefundId, c.Saga.OrderId, c.Saga.AmountMinor, c.Saga.Reason ?? "rma", "rma"))
                        .Publish(c => new RmaStateChanged(c.Saga.CorrelationId, c.Saga.OrderId, c.Saga.Email!, "Approved"))
                        .TransitionTo(RefundPending)),
            When(RmaDeniedEvent)
                // Terminal states are retained as the admin RMA read model (no Finalize).
                .Publish(c => new RmaStateChanged(c.Saga.CorrelationId, c.Saga.OrderId, c.Saga.Email!, "Denied"))
                .TransitionTo(Denied));

        During(AwaitingReturn,
            When(ReturnReceivedEvent)
                .Then(c => c.Saga.RefundId = Guid.CreateVersion7())
                .Publish(c => new RefundRequested(c.Saga.RefundId, c.Saga.OrderId, c.Saga.AmountMinor, c.Saga.Reason ?? "rma", "rma"))
                .TransitionTo(RefundPending));

        During(RefundPending,
            When(RefundCompletedEvent)
                .Publish(c => new RmaStateChanged(c.Saga.CorrelationId, c.Saga.OrderId, c.Saga.Email!, "RefundIssued"))
                .TransitionTo(RefundIssued));

    }
}

// Support-internal saga events (commands from the endpoints).
public record RmaRequested(Guid RmaId, Guid OrderId, string Email, long AmountMinor, string Reason);
public record RmaApproved(Guid RmaId, bool RequireReturn);
public record RmaDenied(Guid RmaId);
public record ReturnReceived(Guid RmaId);
