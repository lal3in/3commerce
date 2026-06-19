using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Projections;

/// <summary>Keeps the local ProductCopy current from Catalog's ProductUpserted events (ADR-0008).</summary>
public sealed class ProductCopyConsumer(OrderingDbContext db) : IConsumer<ProductUpserted>
{
    public async Task Consume(ConsumeContext<ProductUpserted> context)
    {
        var m = context.Message;
        var copy = await db.ProductCopies.FindAsync([m.ProductId], context.CancellationToken);
        if (copy is null)
        {
            db.ProductCopies.Add(new ProductCopy
            {
                ProductId = m.ProductId,
                Slug = m.Slug,
                Title = m.Title,
                MinPriceMinor = m.MinPriceMinor,
                SellingPriceMinor = m.MinPriceMinor,
                Currency = m.Currency,
                ImageUrl = m.ImageUrl,
            });
        }
        else
        {
            copy.Slug = m.Slug;
            copy.Title = m.Title;
            copy.MinPriceMinor = m.MinPriceMinor;
            copy.SellingPriceMinor = m.MinPriceMinor;
            copy.Currency = m.Currency;
            copy.ImageUrl = m.ImageUrl;
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
