using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Projections;

/// <summary>Keeps the local OfferCopy current from Catalog's OfferChanged events (ADR-0008).</summary>
public sealed class OfferChangedConsumer(OrderingDbContext db) : IConsumer<OfferChanged>
{
    public async Task Consume(ConsumeContext<OfferChanged> context)
    {
        var m = context.Message;
        var copy = await db.OfferCopies.SingleOrDefaultAsync(o => o.OfferId == m.OfferId, context.CancellationToken);
        if (copy is null)
        {
            copy = new OfferCopy { OfferId = m.OfferId };
            db.OfferCopies.Add(copy);
        }

        copy.TenantId = m.TenantId;
        copy.ProductId = m.ProductId;
        copy.VariantId = m.VariantId;
        copy.SupplierId = m.SupplierId;
        copy.FulfilmentType = m.FulfilmentType;
        copy.Priority = m.Priority;
        copy.Active = m.Active;
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
