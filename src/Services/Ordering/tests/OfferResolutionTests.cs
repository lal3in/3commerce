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

    // --- Offer-as-price selection (ResolvePricingOffer): storefront + window + currency + active ---

    private static readonly Guid Store = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static OfferCopy PricingOffer(
        Guid? variant, long price, int priority = 0, bool active = true, Guid? storefrontId = null,
        string currency = "EUR", DateTimeOffset? from = null, DateTimeOffset? until = null) =>
        new()
        {
            OfferId = Guid.NewGuid(),
            TenantId = Tenant,
            ProductId = Product,
            VariantId = variant,
            PriceMinor = price,
            Priority = priority,
            Active = active,
            StorefrontId = storefrontId,
            Currency = currency,
            ActiveFrom = from,
            ActiveUntil = until,
        };

    private static long? Price(IEnumerable<OfferCopy> offers, Guid? variant = null, Guid? store = null, string currency = "EUR") =>
        OfferResolution.ResolvePricingOffer(offers, Tenant, Product, variant, store ?? Store, currency, Now)?.PriceMinor;

    [Fact]
    public void No_effective_offer_returns_null_so_checkout_keeps_the_catalog_price() =>
        Assert.Null(Price([]));

    [Fact]
    public void An_all_storefront_offer_prices_any_storefront_of_its_currency() =>
        Assert.Equal(1_500, Price([PricingOffer(null, 1_500, storefrontId: null)]));

    [Fact]
    public void A_storefront_scoped_offer_only_prices_its_own_storefront()
    {
        var offers = new[] { PricingOffer(null, 1_500, storefrontId: Guid.NewGuid()) };
        Assert.Null(Price(offers)); // scoped to a different store
        Assert.Equal(1_500, Price([PricingOffer(null, 1_500, storefrontId: Store)]));
    }

    [Fact]
    public void A_storefront_scoped_offer_beats_an_all_storefront_one_at_the_same_grain()
    {
        var offers = new[]
        {
            PricingOffer(null, 2_000, storefrontId: null),
            PricingOffer(null, 1_200, storefrontId: Store),
        };
        Assert.Equal(1_200, Price(offers));
    }

    [Fact]
    public void A_variant_specific_offer_beats_a_product_level_one()
    {
        var offers = new[] { PricingOffer(null, 2_000), PricingOffer(Variant, 900) };
        Assert.Equal(900, Price(offers, variant: Variant));
    }

    [Fact]
    public void Lowest_priority_wins_among_the_same_grain_and_scope()
    {
        var offers = new[]
        {
            PricingOffer(null, 2_000, priority: 10, storefrontId: Store),
            PricingOffer(null, 1_100, priority: 1, storefrontId: Store),
        };
        Assert.Equal(1_100, Price(offers));
    }

    [Fact]
    public void An_offer_outside_its_active_window_does_not_price()
    {
        Assert.Null(Price([PricingOffer(null, 1_500, from: Now.AddDays(1))])); // not started
        Assert.Null(Price([PricingOffer(null, 1_500, until: Now.AddDays(-1))])); // expired
        Assert.Equal(1_500, Price([PricingOffer(null, 1_500, from: Now.AddDays(-1), until: Now.AddDays(1))]));
    }

    [Fact]
    public void An_offer_in_a_different_currency_does_not_price() =>
        Assert.Null(Price([PricingOffer(null, 1_500, currency: "USD")], currency: "EUR"));

    [Fact]
    public void An_inactive_or_zero_price_offer_does_not_price()
    {
        Assert.Null(Price([PricingOffer(null, 1_500, active: false)]));
        Assert.Null(Price([PricingOffer(null, 0)]));
    }
}
