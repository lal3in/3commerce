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
