using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Pricing.Domain;

namespace ThreeCommerce.Pricing.Tests;

public class PriceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();

    private static Price Flat(long amount = 1000) =>
        Price.Create(Tenant, Product, null, null, amount, "eur", PricingModel.OneTime, BillingPeriod.Once, [], Now);

    [Fact]
    public void Flat_price_is_amount_times_quantity()
    {
        Assert.Equal(5000, Flat(1000).PriceFor(5));
        Assert.Equal("EUR", Flat().Currency);
    }

    [Fact]
    public void Graduated_tiers_price_each_quantity_block()
    {
        var price = Price.Create(
            Tenant, Product, null, null, 0, "eur", PricingModel.Tiered, BillingPeriod.Once, [(1, 100), (11, 80), (101, 60)], Now);

        Assert.Equal(1000, price.PriceFor(10));
        Assert.Equal((10 * 100) + (5 * 80), price.PriceFor(15));
        Assert.Equal((10 * 100) + (90 * 80) + (1 * 60), price.PriceFor(101));
        Assert.Equal(3, price.Tiers.Count);
    }

    [Fact]
    public void Tiers_must_start_at_one_and_be_non_negative()
    {
        Assert.Throws<PricingRuleException>(() => Price.Create(
            Tenant, Product, null, null, 0, "eur", PricingModel.Tiered, BillingPeriod.Once, [(2, 100)], Now));
    }

    [Fact]
    public void Negative_amount_is_rejected()
    {
        Assert.Throws<PricingRuleException>(() => Flat(-1));
    }
}
