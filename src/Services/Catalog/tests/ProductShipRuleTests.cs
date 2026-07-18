using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Tests;

public class ProductShipRuleTests
{
    [Fact]
    public void ShipRules_default_is_empty()
    {
        var product = NewProduct();
        Assert.Empty(product.ShipRules);
    }

    [Fact]
    public void SetShipRules_normalizes_uppercases_dedupes_last_wins_and_sorts_star_first()
    {
        var product = NewProduct();

        product.SetShipRules(
            [
                new ProductShipRule("nz", true, false),
                new ProductShipRule(" au ", true, false),
                new ProductShipRule("*", true, false),
                new ProductShipRule("AU", false, true), // same country later — last wins
            ],
            DateTimeOffset.UtcNow);

        Assert.Equal(["*", "AU", "NZ"], product.ShipRules.Select(r => r.CountryCode));
        var au = product.ShipRules.Single(r => r.CountryCode == "AU");
        Assert.False(au.ChargeDestinationTax);
        Assert.True(au.ShippingCovered);
    }

    [Fact]
    public void SetShipRules_accepts_the_whole_world_sentinel()
    {
        var product = NewProduct();

        product.SetShipRules([new ProductShipRule("*", false, true)], DateTimeOffset.UtcNow);

        var rule = Assert.Single(product.ShipRules);
        Assert.Equal("*", rule.CountryCode);
    }

    [Fact]
    public void SetShipRules_rejects_non_two_letter_codes()
    {
        var product = NewProduct();

        Assert.Throws<CatalogRuleException>(() => product.SetShipRules([new ProductShipRule("AUS", true, false)], DateTimeOffset.UtcNow));
        Assert.Throws<CatalogRuleException>(() => product.SetShipRules([new ProductShipRule("A1", true, false)], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SetShipRules_null_keeps_current_and_empty_clears()
    {
        var product = NewProduct();
        product.SetShipRules([new ProductShipRule("AU", false, false)], DateTimeOffset.UtcNow);

        product.SetShipRules(null, DateTimeOffset.UtcNow); // an older client PUT omits the field
        Assert.Single(product.ShipRules);

        product.SetShipRules([], DateTimeOffset.UtcNow); // explicit empty clears
        Assert.Empty(product.ShipRules);
    }

    [Fact]
    public void SetShipRules_updates_the_timestamp()
    {
        var product = NewProduct();
        var before = product.UpdatedAt;
        var later = before.AddMinutes(5);

        product.SetShipRules([new ProductShipRule("US", true, false)], later);

        Assert.Equal(later, product.UpdatedAt);
    }

    private static Product NewProduct() => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        Slug = "sample",
        Title = "Sample product",
        Brand = "Sample",
        CategoryId = Guid.CreateVersion7(),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
