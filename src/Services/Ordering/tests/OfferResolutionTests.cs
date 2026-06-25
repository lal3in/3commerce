using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

public class OfferResolutionTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Variant = Guid.NewGuid();

    private static OfferCopy Offer(Guid? variant, FulfilmentType type, int priority, bool active = true) =>
        new()
        {
            OfferId = Guid.NewGuid(),
            TenantId = Tenant,
            ProductId = Product,
            VariantId = variant,
            FulfilmentType = type,
            Priority = priority,
            Active = active,
        };

    [Fact]
    public void No_offers_resolves_to_unassigned() =>
        Assert.Equal(FulfilmentType.Unassigned, OfferResolution.ResolveFulfilment([], Tenant, Product, Variant));

    [Fact]
    public void Variant_specific_offer_beats_product_level()
    {
        var offers = new[] { Offer(null, FulfilmentType.Dropship, 0), Offer(Variant, FulfilmentType.Warehouse, 5) };
        Assert.Equal(FulfilmentType.Warehouse, OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant));
    }

    [Fact]
    public void Lowest_priority_wins_among_the_same_grain()
    {
        var offers = new[] { Offer(Variant, FulfilmentType.Dropship, 10), Offer(Variant, FulfilmentType.Warehouse, 1) };
        Assert.Equal(FulfilmentType.Warehouse, OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant));
    }

    [Fact]
    public void Product_level_offer_applies_when_no_variant_specific_one_exists()
    {
        var offers = new[] { Offer(null, FulfilmentType.Dropship, 0) };
        Assert.Equal(FulfilmentType.Dropship, OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant));
    }

    [Fact]
    public void Inactive_and_other_tenant_offers_are_ignored()
    {
        var offers = new[]
        {
            Offer(Variant, FulfilmentType.Warehouse, 1, active: false),
            new OfferCopy
            {
                OfferId = Guid.NewGuid(), TenantId = Guid.NewGuid(), ProductId = Product, VariantId = Variant,
                FulfilmentType = FulfilmentType.Dropship, Priority = 0, Active = true,
            },
        };
        Assert.Equal(FulfilmentType.Unassigned, OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant));
    }
}
