using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Tests;

public class OfferTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Supplier = Guid.NewGuid();

    private static Offer Warehouse(long price = 1000) =>
        Offer.Create(Tenant, Product, Guid.NewGuid(), Supplier, SupplyCategory.Physical, FulfilmentType.Warehouse, price, "eur", 0, Now);

    private static Offer Digital(FulfilmentType type, long price) =>
        Offer.Create(Tenant, Product, Guid.NewGuid(), Supplier, SupplyCategory.Digital, type, price, "eur", 0, Now);

    [Fact]
    public void PriceFor_flat_is_unit_price_times_quantity()
    {
        Assert.Equal(5000, Warehouse(1000).PriceFor(5));
        Assert.Equal(0, Warehouse(1000).PriceFor(0));
    }

    [Fact]
    public void Graduated_tiers_price_each_quantity_block()
    {
        var offer = Digital(FulfilmentType.Usage, 0);
        offer.SetPricing(PricingModel.Tiered, BillingPeriod.Once, [(1, 100), (11, 80), (101, 60)], Now);

        Assert.Equal(1000, offer.PriceFor(10));                       // 10 × 100
        Assert.Equal((10 * 100) + (5 * 80), offer.PriceFor(15));      // 1400
        Assert.Equal((10 * 100) + (90 * 80) + (1 * 60), offer.PriceFor(101)); // 8260
    }

    [Fact]
    public void SetPricing_rejects_tiers_not_starting_at_one()
    {
        Assert.Throws<CatalogRuleException>(
            () => Warehouse().SetPricing(PricingModel.Tiered, BillingPeriod.Once, [(2, 100)], Now));
    }

    [Fact]
    public void Subscription_pricing_sets_the_billing_period_and_keeps_a_per_period_price()
    {
        var offer = Digital(FulfilmentType.Subscription, 1500);
        offer.SetPricing(PricingModel.Subscription, BillingPeriod.Monthly, [], Now);
        Assert.Equal(BillingPeriod.Monthly, offer.BillingPeriod);
        Assert.Equal(PricingModel.Subscription, offer.PricingModel);
        Assert.Equal(1500, offer.PriceFor(1));
    }

    [Fact]
    public void Create_sets_defaults_and_uppercases_currency()
    {
        var offer = Warehouse();
        Assert.NotEqual(Guid.Empty, offer.Id);
        Assert.Equal(SupplyCategory.Physical, offer.SupplyCategory);
        Assert.Equal(FulfilmentType.Warehouse, offer.FulfilmentType);
        Assert.Equal(PricingModel.OneTime, offer.PricingModel);
        Assert.Equal("EUR", offer.Currency);
        Assert.True(offer.IsActive);
    }

    [Theory]
    [InlineData(SupplyCategory.Physical, FulfilmentType.DigitalDownload)]
    [InlineData(SupplyCategory.Physical, FulfilmentType.Subscription)]
    [InlineData(SupplyCategory.Digital, FulfilmentType.Warehouse)]
    [InlineData(SupplyCategory.Digital, FulfilmentType.Dropship)]
    [InlineData(SupplyCategory.Service, FulfilmentType.Warehouse)]
    public void Create_rejects_incompatible_category_and_fulfilment(SupplyCategory category, FulfilmentType type)
    {
        var ex = Assert.Throws<CatalogRuleException>(
            () => Offer.Create(Tenant, Product, null, Supplier, category, type, 500, "EUR", 0, Now));
        Assert.Contains("not valid", ex.Message);
    }

    [Theory]
    [InlineData(SupplyCategory.Physical, FulfilmentType.Dropship)]
    [InlineData(SupplyCategory.Digital, FulfilmentType.Subscription)]
    [InlineData(SupplyCategory.Digital, FulfilmentType.Usage)]
    [InlineData(SupplyCategory.Service, FulfilmentType.ManualService)]
    public void Create_accepts_compatible_category_and_fulfilment(SupplyCategory category, FulfilmentType type)
    {
        var offer = Offer.Create(Tenant, Product, null, Supplier, category, type, 500, "EUR", 0, Now);
        Assert.Equal(type, offer.FulfilmentType);
    }

    [Fact]
    public void Create_rejects_negative_price_and_missing_ids()
    {
        Assert.Throws<CatalogRuleException>(
            () => Offer.Create(Tenant, Product, null, Supplier, SupplyCategory.Physical, FulfilmentType.Warehouse, -1, "EUR", 0, Now));
        Assert.Throws<CatalogRuleException>(
            () => Offer.Create(Tenant, Product, null, Guid.Empty, SupplyCategory.Physical, FulfilmentType.Warehouse, 1, "EUR", 0, Now));
    }

    [Fact]
    public void SetPrice_updates_and_guards_negative()
    {
        var offer = Warehouse();
        offer.SetPrice(2500, Now.AddHours(1));
        Assert.Equal(2500, offer.PriceMinor);
        Assert.Throws<CatalogRuleException>(() => offer.SetPrice(-5, Now));
    }

    [Fact]
    public void Deactivate_then_activate_toggles_status()
    {
        var offer = Warehouse();
        offer.Deactivate(Now);
        Assert.False(offer.IsActive);
        offer.Activate(Now);
        Assert.True(offer.IsActive);
    }
}
