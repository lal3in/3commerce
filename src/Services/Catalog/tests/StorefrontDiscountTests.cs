using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Tests;

/// <summary>
/// The storefront-wide discount (basis points, 0–10000; 0 = none): a validated setter with the
/// null-leaves-untouched convention (an older admin client that doesn't send the field can't wipe it),
/// carried by DuplicateFrom into a clone.
/// </summary>
public class StorefrontDiscountTests
{
    private static Storefront NewStorefront() =>
        Storefront.Create(Guid.CreateVersion7(), "Discount store", DateTimeOffset.UtcNow);

    [Fact]
    public void SetDiscount_stores_the_basis_points()
    {
        var storefront = NewStorefront();

        storefront.SetDiscount(1_500, DateTimeOffset.UtcNow);

        Assert.Equal(1_500, storefront.DiscountBasisPoints);
    }

    [Fact]
    public void SetDiscount_defaults_to_zero()
    {
        Assert.Equal(0, NewStorefront().DiscountBasisPoints);
    }

    [Fact]
    public void SetDiscount_null_leaves_the_current_value()
    {
        var storefront = NewStorefront();
        storefront.SetDiscount(2_000, DateTimeOffset.UtcNow);

        storefront.SetDiscount(null, DateTimeOffset.UtcNow);

        Assert.Equal(2_000, storefront.DiscountBasisPoints);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void SetDiscount_rejects_out_of_range_bps(int bps)
    {
        var storefront = NewStorefront();

        Assert.Throws<CatalogRuleException>(() => storefront.SetDiscount(bps, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_000)]
    public void SetDiscount_accepts_the_range_bounds(int bps)
    {
        var storefront = NewStorefront();

        storefront.SetDiscount(bps, DateTimeOffset.UtcNow);

        Assert.Equal(bps, storefront.DiscountBasisPoints);
    }

    [Fact]
    public void DuplicateFrom_carries_the_source_discount()
    {
        var source = NewStorefront();
        source.SetDiscount(1_000, DateTimeOffset.UtcNow);

        var clone = Storefront.DuplicateFrom(source, "Discount store (copy)", DateTimeOffset.UtcNow);

        Assert.Equal(1_000, clone.DiscountBasisPoints);
    }
}
