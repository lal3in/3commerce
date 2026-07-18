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
        var copy = await db.ProductCopies.Include(p => p.Variants).ThenInclude(v => v.Prices)
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
                ShipRules = m.ShipRules?.Select(r => new ProductShipRule(r.CountryCode, r.ChargeDestinationTax, r.ShippingCovered)).ToList() ?? [],
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
            copy.ShipRules = m.ShipRules?.Select(r => new ProductShipRule(r.CountryCode, r.ChargeDestinationTax, r.ShippingCovered)).ToList() ?? [];
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

            // Reconcile per-currency prices (tenant-authored; drives storefront-currency cart/checkout).
            var keptCurrencies = new HashSet<string>();
            foreach (var pr in variant.Prices ?? [])
            {
                var cur = pr.Currency.Trim().ToUpperInvariant();
                var ep = existing.Prices.FirstOrDefault(p => p.Currency == cur);
                if (ep is null)
                {
                    ep = new ProductVariantCopyPrice { Id = Guid.CreateVersion7(), VariantId = existing.VariantId, Currency = cur, PriceMinor = pr.PriceMinor };
                    existing.Prices.Add(ep);
                    db.ProductVariantCopyPrices.Add(ep);
                }
                else
                {
                    ep.PriceMinor = pr.PriceMinor;
                }

                keptCurrencies.Add(cur);
            }

            db.ProductVariantCopyPrices.RemoveRange(existing.Prices.Where(p => !keptCurrencies.Contains(p.Currency)));
            kept.Add(variant.VariantId);
        }

        var removed = copy.Variants.Where(v => !kept.Contains(v.VariantId)).ToList();
        db.ProductVariantCopies.RemoveRange(removed);
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
