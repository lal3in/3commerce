using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Infrastructure.Consumers;

/// <summary>
/// Closes out an order's shipments when Ordering reports it delivered (a fulfilling supplier or an admin
/// marked it delivered in the supplier portal). Every shipment for the order moves to
/// <see cref="ShipmentStatus.Delivered"/> — including a "Collect at warehouse" order's warehouse shipment,
/// which is picked up rather than dispatched. Idempotent: a shipment already Delivered is left as-is, and
/// an order with no shipments (e.g. digital-only) is a harmless no-op.
/// </summary>
public sealed class OrderDeliveredConsumer(FulfillmentDbContext db) : IConsumer<OrderDelivered>
{
    public async Task Consume(ConsumeContext<OrderDelivered> context)
    {
        var m = context.Message;
        var shipments = await db.Shipments
            .Where(s => s.OrderId == m.OrderId && s.Status != ShipmentStatus.Delivered)
            .ToListAsync(context.CancellationToken);
        if (shipments.Count == 0)
        {
            return;
        }

        foreach (var shipment in shipments)
        {
            shipment.Status = ShipmentStatus.Delivered;
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
