using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Support.Domain;

namespace ThreeCommerce.Support.Infrastructure.Consumers;

/// <summary>Keeps a local order read-copy so RMA amounts are derived server-side (BL-8).</summary>
public sealed class OrderSnapshotConsumer(SupportDbContext db) : IConsumer<OrderConfirmed>
{
    public async Task Consume(ConsumeContext<OrderConfirmed> context)
    {
        var m = context.Message;
        if (await db.OrderSnapshots.AnyAsync(o => o.OrderId == m.OrderId, context.CancellationToken))
        {
            return;
        }

        db.OrderSnapshots.Add(new OrderSnapshot
        {
            OrderId = m.OrderId,
            Email = m.Email,
            GrossMinor = m.AmountMinor,
            Currency = m.Currency,
            Lines = m.Lines.Select(l => new OrderSnapshotLine
            {
                Id = Guid.CreateVersion7(),
                OrderId = m.OrderId,
                ProductId = l.ProductId,
                Title = l.Title,
                UnitPriceMinor = l.UnitPriceMinor,
                Quantity = l.Quantity,
            }).ToList(),
        });
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
