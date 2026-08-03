using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Tests;

public class ProductTypeShippingPolicyTests
{
    [Fact]
    public void Default_policy_ships_physical_only()
    {
        var policy = ProductTypeShippingPolicy.Create(Guid.CreateVersion7(), DateTimeOffset.UtcNow);

        Assert.True(policy.RequiresShipping(ProductType.Physical));
        Assert.False(policy.RequiresShipping(ProductType.Digital));
        Assert.False(policy.RequiresShipping(ProductType.Service));
    }

    [Fact]
    public void SetTypes_replaces_the_shippable_set_and_round_trips()
    {
        var policy = ProductTypeShippingPolicy.Create(Guid.CreateVersion7(), DateTimeOffset.UtcNow);

        policy.SetTypes([ProductType.Physical, ProductType.Bundle], DateTimeOffset.UtcNow);

        Assert.True(policy.RequiresShipping(ProductType.Physical));
        Assert.True(policy.RequiresShipping(ProductType.Bundle));
        Assert.False(policy.RequiresShipping(ProductType.Digital));
        // Persisted form is a stable, ordered CSV of enum names.
        Assert.Equal("Physical,Bundle", policy.RequiresShippingTypes);
    }

    [Fact]
    public void SetTypes_empty_marks_nothing_shippable()
    {
        var policy = ProductTypeShippingPolicy.Create(Guid.CreateVersion7(), DateTimeOffset.UtcNow);

        policy.SetTypes([], DateTimeOffset.UtcNow);

        Assert.All(Enum.GetValues<ProductType>(), t => Assert.False(policy.RequiresShipping(t)));
    }

    [Fact]
    public void Readiness_requires_dimensions_only_when_the_policy_says_the_type_ships()
    {
        var product = ShippableProductMissingDimensions();
        var publication = ProductPublication.Assign(product.TenantId, Guid.CreateVersion7(), product, DateTimeOffset.UtcNow);
        publication.SetFulfillment(FulfilmentType.Dropship, "au", "8518", DateTimeOffset.UtcNow);

        // Policy says this type does NOT ship → dimensions are not required → ready.
        Assert.True(publication.CheckReadiness(product, requiresShipping: false).IsReady);

        // Policy says it DOES ship → the missing package dimensions block publication.
        var shippable = publication.CheckReadiness(product, requiresShipping: true);
        Assert.False(shippable.IsReady);
        Assert.Contains("product and package weight + dimensions on every shippable variant", shippable.MissingRequirements);
    }

    private static Product ShippableProductMissingDimensions()
    {
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            Slug = "sample",
            Title = "Sample product",
            Brand = "Sample",
            CategoryId = Guid.CreateVersion7(),
            ImageUrls = ["https://example.test/sample.png"],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        // No weight/dimensions on the variant.
        product.Variants.Add(new Variant
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            Sku = "SKU-1",
            PriceMinor = 1000,
            Currency = "AUD",
            StockQuantity = 10,
        });
        return product;
    }
}
