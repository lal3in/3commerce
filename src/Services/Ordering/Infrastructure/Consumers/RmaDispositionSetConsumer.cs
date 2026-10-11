using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Support;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Consumers;

/// <summary>
/// Values an RMA's returned goods so Payments can correct the COGS accrual (phase 1). Support knows the
/// whole-order RMA (order id + refunded gross) but not per-line supplier costs; Ordering owns cost
/// knowledge, so it values the order's goods at exactly what <see cref="OrderStatusConsumer"/> ACCRUED for
/// them — each line's <see cref="OrderLine.BookedSupplierCostMinor"/>, recorded with the accrual — and
/// republishes <see cref="ReturnedGoodsValued"/>. Offers are never re-resolved here: an offer edited,
/// re-prioritised, re-scoped or deactivated since the sale must not change what this order's return
/// reverses (ADR-0059), so a booking and its reversal can never diverge.
/// <para>
/// The RMA reports a refunded gross, not returned line quantities, so — as the plan allows for an
/// amount-only return — the returned goods are scaled proportionally by the refunded share of the order
/// (a whole-order RMA scales by 1). Nothing is published when the returned value rounds to zero: there
/// is then no accrual slice to correct.
/// </para>
/// </summary>
public sealed class RmaDispositionSetConsumer(
    OrderingDbContext db, ILogger<RmaDispositionSetConsumer> logger) : IConsumer<RmaDispositionSet>
{
    public async Task Consume(ConsumeContext<RmaDispositionSet> context)
    {
        var msg = context.Message;

        var order = await db.Orders.Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Id == msg.OrderId, context.CancellationToken);
        if (order is null)
        {
            logger.LogInformation("RmaDispositionSet: order {OrderId} not found for RMA {RmaId}; skipping", msg.OrderId, msg.RmaId);
            return;
        }

        // Total GROSS supplier cost ACCRUED for the order's goods (order currency), as booked at confirmation.
        // No supplier cost accrued → nothing to correct.
        var totalGross = order.Lines.Sum(l => Math.Max(0, l.BookedSupplierCostMinor));
        if (totalGross <= 0)
        {
            return;
        }

        // Scale by the refunded share (whole-order RMA → 1). Amount-only proportional scaling, since the
        // RMA carries a refunded gross rather than returned quantities (plan: proportional when only an
        // amount share is known).
        var proportion = order.GrossMinor <= 0
            ? 1m
            : Math.Min(1m, (decimal)msg.RefundedMinor / order.GrossMinor);
        var costMinor = (long)Math.Round(totalGross * proportion, MidpointRounding.ToEven);
        if (costMinor <= 0)
        {
            return; // nothing to correct for this return
        }

        await context.Publish(
            new ReturnedGoodsValued(
                msg.RmaId, order.Id, order.StorefrontId, order.TenantId,
                costMinor, order.Currency, msg.Kind, msg.StorageReason, msg.Revision),
            context.CancellationToken);
    }
}
