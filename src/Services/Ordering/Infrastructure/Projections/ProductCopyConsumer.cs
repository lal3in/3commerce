using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Projections;

/// <summary>Keeps the local ProductCopy current from Catalog's ProductUpserted events (ADR-0008).</summary>
public sealed class ProductCopyConsumer(OrderingDbContext db) : IConsumer<ProductUpserted>
{
    public async Task Consume(ConsumeContext<ProductUpserted> context)
    {
        var m = context.Message;
        var copy = await db.ProductCopies.Include(p => p.Variants)
            .SingleOrDefaultAsync(p => p.ProductId == m.ProductId, context.CancellationToken);
        if (copy is null)
        {
            copy = new ProductCopy
            {
                ProductId = m.ProductId,
                Slug = m.Slug,
                Title = m.Title,
                MinPriceMinor = m.MinPriceMinor,
                SellingPriceMinor = m.MinPriceMinor,
                Currency = m.Currency,
                ImageUrl = m.ImageUrl,
            };
            db.ProductCopies.Add(copy);
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

        var kept = new HashSet<Guid>();
        foreach (var variant in m.Variants)
        {
            var existing = copy.Variants.FirstOrDefault(v => v.VariantId == variant.VariantId);
            if (existing is null)
            {
                existing = new ProductVariantCopy
                {
                    VariantId = variant.VariantId,
                    ProductId = m.ProductId,
                    Sku = variant.Sku,
                    Currency = variant.Currency,
                };
                copy.Variants.Add(existing);
                db.ProductVariantCopies.Add(existing);
            }

            existing.Sku = variant.Sku;
            existing.PriceMinor = variant.PriceMinor;
            existing.Currency = variant.Currency;
            existing.StockQuantity = variant.StockQuantity;
            kept.Add(variant.VariantId);
        }

        var removed = copy.Variants.Where(v => !kept.Contains(v.VariantId)).ToList();
        db.ProductVariantCopies.RemoveRange(removed);
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
