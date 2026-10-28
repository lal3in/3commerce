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
            copy = new StorefrontTaxCopy { StorefrontId = m.StorefrontId, Currency = m.Currency };
            db.StorefrontTaxCopies.Add(copy);
        }

        Apply(copy, m);
        await db.SaveChangesAsync(context.CancellationToken);
    }

    /// <summary>
    /// Copies a config snapshot onto the read copy. Fields a pre-change publisher did not send arrive as their
    /// contract defaults (e.g. ADR-0063 P2 TaxJurisdiction = ""), so an old message still applies cleanly.
    /// </summary>
    public static void Apply(StorefrontTaxCopy copy, StorefrontConfigChanged m)
    {
        copy.TenantId = m.TenantId;
        copy.Currency = m.Currency;
        copy.TaxRateBasisPoints = m.TaxRateBasisPoints;
        copy.IsLive = m.IsLive;
        copy.TaxInclusive = m.TaxInclusive;
        copy.ShipToCountries = m.ShipToCountries?.ToList() ?? [];
        copy.DiscountBasisPoints = m.DiscountBps;
        copy.TaxJurisdiction = m.TaxJurisdiction ?? string.Empty;
        copy.SellerEstablishmentKey = m.SellerEstablishmentKey ?? string.Empty;
    }
}
