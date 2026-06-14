using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Infrastructure.Consumers;

/// <summary>
/// Turns a confirmed order into shipments, one per fulfillment source (ADR-0003).
/// Idempotent: a redelivered OrderConfirmed does not create duplicate shipments.
/// </summary>
public sealed class OrderConfirmedConsumer(FulfillmentDbContext db, TimeProvider time) : IConsumer<OrderConfirmed>
{
    public async Task Consume(ConsumeContext<OrderConfirmed> context)
    {
        var m = context.Message;
        if (await db.Shipments.AnyAsync(s => s.OrderId == m.OrderId, context.CancellationToken))
        {
            return;
        }

        foreach (var group in m.Lines.GroupBy(l => l.FulfillmentSource))
        {
            var shipment = new Shipment
            {
                Id = Guid.CreateVersion7(),
                OrderId = m.OrderId,
                FulfillmentSource = group.Key,
                Status = ShipmentStatus.Created,
                Email = m.Email,
                CreatedAt = time.GetUtcNow(),
                Lines = group.Select(l => new ShipmentLine
                {
                    Id = Guid.CreateVersion7(),
                    ProductId = l.ProductId,
                    Title = l.Title,
                    Quantity = l.Quantity,
                }).ToList(),
            };
            db.Shipments.Add(shipment);
            await context.Publish(new ShipmentCreated(shipment.Id, m.OrderId, group.Key));
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
