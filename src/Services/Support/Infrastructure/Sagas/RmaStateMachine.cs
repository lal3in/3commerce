using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Support;

namespace ThreeCommerce.Support.Infrastructure.Sagas;

/// <summary>
/// RMA lifecycle (ADR-0018): Requested → Approved/Denied → (AwaitingReturn → ReturnReceived)
/// → RefundIssued. Approval publishes the single Phase-3 RefundRequested contract — Support
/// never touches Stripe or the ledger directly. RefundCompleted advances to RefundIssued.
/// <para>
/// RefundFailed is the other terminal outcome: Payments could not execute the refund (no captured
/// payment, more than the remaining balance, or a provider decline). It used to be a silent return in
/// Payments, so the RMA stayed in RefundPending forever; now it lands in RefundFailed and the shopper
/// (and the RMA queue) are told, so an operator can retry with a corrected amount (rma_disc).
/// </para>
/// </summary>
public sealed class RmaStateMachine : MassTransitStateMachine<RmaState>
{
    public State Requested { get; private set; } = null!;
    public State AwaitingReturn { get; private set; } = null!;
    public State RefundPending { get; private set; } = null!;
    public State Denied { get; private set; } = null!;
    public State RefundIssued { get; private set; } = null!;
    public State RefundFailed { get; private set; } = null!;

    public Event<RmaRequested> RmaRequestedEvent { get; private set; } = null!;
    public Event<RmaApproved> RmaApprovedEvent { get; private set; } = null!;
    public Event<RmaDenied> RmaDeniedEvent { get; private set; } = null!;
    public Event<ReturnReceived> ReturnReceivedEvent { get; private set; } = null!;
    public Event<RefundCompleted> RefundCompletedEvent { get; private set; } = null!;
    public Event<RefundFailed> RefundFailedEvent { get; private set; } = null!;

    public RmaStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => RmaRequestedEvent, e => e.CorrelateById(c => c.Message.RmaId));
        Event(() => RmaApprovedEvent, e => e.CorrelateById(c => c.Message.RmaId));
        Event(() => RmaDeniedEvent, e => e.CorrelateById(c => c.Message.RmaId));
        Event(() => ReturnReceivedEvent, e => e.CorrelateById(c => c.Message.RmaId));
        // RefundCompleted carries the RefundId we generated; match it back to the saga.
        Event(() => RefundCompletedEvent, e => e.CorrelateBy((saga, ctx) => saga.RefundId == ctx.Message.RefundId));
        // ...and so does its failure twin. Not correlating it would put the refund's own failure on the
        // error queue and leave the RMA exactly where the silent return left it.
        Event(() => RefundFailedEvent, e => e.CorrelateBy((saga, ctx) => saga.RefundId == ctx.Message.RefundId));

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
                // Admin-initiated refunds arrive pre-approved (AutoApprove): skip the manual review and
                // go straight down the no-return refund path, so the refund still travels the single
                // RefundRequested → RefundCompleted route and shows in the RMA queue as RefundPending →
                // RefundIssued. Customer-raised RMAs (AutoApprove=false) wait for an operator decision.
                .IfElse(c => c.Message.AutoApprove,
                    auto => auto
                        .Then(c => c.Saga.RefundId = Guid.CreateVersion7())
                        .Publish(c => new RefundRequested(c.Saga.RefundId, c.Saga.OrderId, c.Saga.AmountMinor, c.Saga.Reason ?? "rma", "rma"))
                        .Publish(c => new RmaStateChanged(c.Saga.CorrelationId, c.Saga.OrderId, c.Saga.Email!, "Approved"))
                        .TransitionTo(RefundPending),
                    manual => manual
                        .Publish(c => new RmaStateChanged(c.Saga.CorrelationId, c.Saga.OrderId, c.Saga.Email!, "Requested"))
                        .TransitionTo(Requested)));

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
                .Then(c => c.Saga.ReturnReceivedAt = DateTimeOffset.UtcNow)
                .Then(c => c.Saga.RefundId = Guid.CreateVersion7())
                .Publish(c => new RefundRequested(c.Saga.RefundId, c.Saga.OrderId, c.Saga.AmountMinor, c.Saga.Reason ?? "rma", "rma"))
                .TransitionTo(RefundPending));

        During(RefundPending,
            When(RefundCompletedEvent)
                .Publish(c => new RmaStateChanged(c.Saga.CorrelationId, c.Saga.OrderId, c.Saga.Email!, "RefundIssued"))
                .TransitionTo(RefundIssued),
            // Terminal, and retained as the admin read model (like Denied): the operator sees a
            // RefundFailed row with the reason instead of a queue entry that never moves.
            When(RefundFailedEvent)
                .Then(c => c.Saga.RefundFailureReason = $"{c.Message.Reason}: {c.Message.Detail}")
                .Publish(c => new RmaStateChanged(c.Saga.CorrelationId, c.Saga.OrderId, c.Saga.Email!, "RefundFailed"))
                .TransitionTo(RefundFailed));

    }
}

// Support-internal saga events (commands from the endpoints).
public record RmaRequested(Guid RmaId, Guid OrderId, string Email, long AmountMinor, string Reason, bool AutoApprove = false);
public record RmaApproved(Guid RmaId, bool RequireReturn);
public record RmaDenied(Guid RmaId);
public record ReturnReceived(Guid RmaId);
