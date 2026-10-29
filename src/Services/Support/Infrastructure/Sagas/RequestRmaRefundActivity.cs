using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Support.Domain;

namespace ThreeCommerce.Support.Infrastructure.Sagas;

/// <summary>
/// Publishes the RMA's <see cref="RefundRequested"/> — the single refund contract (ADR-0014) — with the order lines the
/// money is for (ADR-0063 P5). The lines come from the RMA's own <see cref="RmaRequestRecord"/> (same id as the saga),
/// written in the request's transaction before the saga ever sees <c>RmaRequested</c>, so they are always there to read;
/// <see cref="RmaRefundAttribution"/> turns them into per-line shares that sum to the amount requested.
/// <para>
/// An RMA whose lines cannot be attributed (a snapshot projected before P5) still refunds exactly as before — the same
/// amount, just without lines, i.e. amount-only. The saga sets <see cref="RmaState.RefundId"/> before this runs.
/// </para>
/// </summary>
public sealed class RequestRmaRefundActivity(SupportDbContext db) : IStateMachineActivity<RmaState>
{
    public void Probe(ProbeContext context) => context.CreateScope("request-rma-refund");

    public void Accept(StateMachineVisitor visitor) => visitor.Visit(this);

    public async Task Execute(BehaviorContext<RmaState> context, IBehavior<RmaState> next)
    {
        await PublishAsync(context);
        await next.Execute(context);
    }

    public async Task Execute<T>(BehaviorContext<RmaState, T> context, IBehavior<RmaState, T> next)
        where T : class
    {
        await PublishAsync(context);
        await next.Execute(context);
    }

    public Task Faulted<TException>(BehaviorExceptionContext<RmaState, TException> context, IBehavior<RmaState> next)
        where TException : Exception => next.Faulted(context);

    public Task Faulted<T, TException>(BehaviorExceptionContext<RmaState, T, TException> context, IBehavior<RmaState, T> next)
        where T : class
        where TException : Exception => next.Faulted(context);

    private async Task PublishAsync(BehaviorContext<RmaState> context)
    {
        var saga = context.Saga;
        var lines = await db.RmaRequestLines.AsNoTracking()
            .Where(l => l.RmaId == saga.CorrelationId)
            .OrderBy(l => l.Id)
            .ToListAsync(context.CancellationToken);
        await context.Publish(Build(saga, lines));
    }

    /// <summary>The refund request for <paramref name="saga"/> given its recorded lines. Pure, for the unit lane.</summary>
    public static RefundRequested Build(RmaState saga, IReadOnlyList<RmaRequestLine> lines)
    {
        var shares = RmaRefundAttribution.For(lines, saga.AmountMinor);
        return new RefundRequested(
            saga.RefundId, saga.OrderId, saga.AmountMinor, saga.Reason ?? "rma", "rma",
            Lines: shares?.Select(s => new RefundLine(s.OrderLineId, s.Quantity, s.AmountMinor)).ToList(),
            // An RMA returns goods: its lines carry the whole amount and no shipping is credited.
            ShippingMinor: shares is null ? null : 0,
            Kind: RefundKind.Return);
    }
}
