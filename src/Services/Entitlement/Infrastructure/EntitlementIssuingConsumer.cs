using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using EntitlementRecord = ThreeCommerce.Entitlement.Domain.Entitlement;

namespace ThreeCommerce.Entitlement.Infrastructure;

/// <summary>
/// Issues entitlements for a confirmed order's digital/service lines (mt7_2). Subscribes to OrderConfirmed
/// independently of Fulfillment (which handles physical shipments). Idempotent per order.
/// </summary>
public sealed class EntitlementIssuingConsumer(EntitlementDbContext db, TimeProvider time) : IConsumer<OrderConfirmed>
{
    public async Task Consume(ConsumeContext<OrderConfirmed> context)
    {
        var m = context.Message;
        if (await db.Entitlements.AnyAsync(e => e.OrderId == m.OrderId, context.CancellationToken))
        {
            return;
        }

        var issued = false;
        foreach (var line in m.Lines)
        {
            var entitlement = EntitlementRecord.Issue(
                m.TenantId, m.OrderId, m.Email, line.ProductId, line.VariantId, line.FulfilmentType, time.GetUtcNow());
            if (entitlement is not null)
            {
                db.Entitlements.Add(entitlement);
                issued = true;
            }
        }

        if (issued)
        {
            await db.SaveChangesAsync(context.CancellationToken);
        }
    }
}
