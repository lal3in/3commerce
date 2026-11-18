using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Consumers;

/// <summary>
/// Counts a settled renewal toward its seller's EU distance-sales threshold (ADR-0063 § 16, OSS threshold crossing).
/// A subscription's renewal is a new supply each period (§ 7.1): when its line was supplied into another Member State,
/// the renewal is an intra-EU distance sale like the checkout sale was, so it adds a row to <c>ordering."EuDistanceSales"</c>
/// (<see cref="EuDistanceSale.ForRenewal"/>) — keyed by the renewal's ledger reference, so a redelivery counts once — and
/// publishes it (<see cref="EuDistanceSaleRecorded"/>) through the same outbox transaction, so Payments' copy of the
/// running total gets it too.
/// </summary>
public sealed class EuDistanceRenewalConsumer(OrderingDbContext db, TimeProvider clock) : IConsumer<SubscriptionRenewalSettled>
{
    public async Task Consume(ConsumeContext<SubscriptionRenewalSettled> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == m.OrderId, ct);
        var line = order?.Lines.FirstOrDefault(l => l.ProductId == m.ProductId && l.VariantId == m.VariantId);
        if (order is null || line is null)
        {
            return; // not an order this service knows (a renewal of a subscription set up by hand): nothing crossed a border here
        }

        var sale = EuDistanceSale.ForRenewal(
            order, line, m.ChargeReference, m.Currency, m.NetMinor, (TaxScheme)(int)m.TaxScheme, m.SettledAt, clock.GetUtcNow());
        if (sale is null
            || await db.EuDistanceSales.AnyAsync(
                s => s.OrderId == sale.OrderId && s.DestinationCountry == sale.DestinationCountry && s.SourceReference == sale.SourceReference, ct))
        {
            return;
        }

        db.EuDistanceSales.Add(sale);
        await db.SaveChangesAsync(ct);
        await context.Publish(EuDistanceSaleEvents.Recorded(sale), ct);
    }
}

/// <summary>The one mapping of a running-total row to its event (checkout and renewals publish the same shape).</summary>
public static class EuDistanceSaleEvents
{
    public static EuDistanceSaleRecorded Recorded(EuDistanceSale sale) => new(
        sale.TenantId, sale.OrderId, sale.SourceReference, sale.HomeCountry, sale.DestinationCountry, sale.Year, sale.Currency,
        sale.NetMinor, sale.RecordedAt);
}
