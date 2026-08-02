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
/// knowledge, so it resolves the order's supplier lines exactly as <see cref="OrderStatusConsumer"/>
/// accrued them (unit <see cref="OfferCopy.SupplierCostMinor"/> × quantity, via <see cref="OfferResolution"/>,
/// relabelled into the order's currency with no FX feed) and republishes <see cref="ReturnedGoodsValued"/>.
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

        var supplierLines = order.Lines.Where(l => l.SupplierId is { } sid && sid != Guid.Empty).ToList();
        if (supplierLines.Count == 0)
        {
            return; // no supplier cost on this order → nothing accrued, nothing to correct
        }

        var productIds = supplierLines.Select(l => l.ProductId).Distinct().ToList();
        var offerCopies = await db.OfferCopies.AsNoTracking()
            .Where(o => o.TenantId == order.TenantId && productIds.Contains(o.ProductId))
            .ToListAsync(context.CancellationToken);

        // Total GROSS supplier cost of the order's goods (same resolution that fed the COGS accrual). The
        // no-FX relabel already happened at accrual time; we just re-derive the order-currency total here.
        long totalGross = 0;
        foreach (var line in supplierLines)
        {
            var offer = OfferResolution.ResolveOffer(offerCopies, order.TenantId, line.ProductId, line.VariantId);
            var lineCost = offer is null ? 0 : offer.SupplierCostMinor * line.Quantity;
            if (lineCost > 0)
            {
                totalGross += lineCost;
            }
        }

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
