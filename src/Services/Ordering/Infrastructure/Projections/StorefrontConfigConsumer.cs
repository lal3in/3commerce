using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Projections;

/// <summary>Keeps the local StorefrontTaxCopy current from Catalog's StorefrontConfigChanged events (ADR-0008).</summary>
public sealed class StorefrontConfigConsumer(OrderingDbContext db) : IConsumer<StorefrontConfigChanged>
{
    public async Task Consume(ConsumeContext<StorefrontConfigChanged> context)
    {
        var m = context.Message;
        var copy = await db.StorefrontTaxCopies.FindAsync([m.StorefrontId], context.CancellationToken);
        if (copy is null)
        {
            copy = new StorefrontTaxCopy
            {
                StorefrontId = m.StorefrontId,
                TenantId = m.TenantId,
                Currency = m.Currency,
                TaxRateBasisPoints = m.TaxRateBasisPoints,
                IsLive = m.IsLive,
                TaxInclusive = m.TaxInclusive,
                ShipToCountries = m.ShipToCountries?.ToList() ?? [],
            };
            db.StorefrontTaxCopies.Add(copy);
        }
        else
        {
            copy.TenantId = m.TenantId;
            copy.Currency = m.Currency;
            copy.TaxRateBasisPoints = m.TaxRateBasisPoints;
            copy.IsLive = m.IsLive;
            copy.TaxInclusive = m.TaxInclusive;
            copy.ShipToCountries = m.ShipToCountries?.ToList() ?? [];
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
