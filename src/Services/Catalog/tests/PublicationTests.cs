using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Tests;

public class PublicationTests
{
    [Fact]
    public void PublicationReadiness_requires_fulfillment_source()
    {
        var product = NewProduct();
        var publication = ProductPublication.Assign(product.TenantId, Guid.CreateVersion7(), product, DateTimeOffset.UtcNow);

        var readiness = publication.CheckReadiness(product);

        Assert.False(readiness.IsReady);
        Assert.Contains("assigned fulfillment source", readiness.MissingRequirements);
    }

    [Fact]
    public void PublicationReadiness_requires_visible_variant()
    {
        var product = NewProduct();
        var publication = ProductPublication.Assign(product.TenantId, Guid.CreateVersion7(), product, DateTimeOffset.UtcNow);
        publication.SetFulfillment(FulfilmentType.Dropship, "au", "8518", DateTimeOffset.UtcNow);
        foreach (var variant in publication.Variants)
        {
            variant.Visible = false;
        }

        var readiness = publication.CheckReadiness(product);

        Assert.False(readiness.IsReady);
        Assert.Contains("at least one visible variant", readiness.MissingRequirements);
    }

    [Fact]
    public void Publication_can_publish_when_ready()
    {
        var product = NewProduct();
        var publication = ProductPublication.Assign(product.TenantId, Guid.CreateVersion7(), product, DateTimeOffset.UtcNow);
        publication.SetFulfillment(FulfilmentType.Dropship, "au", "8518", DateTimeOffset.UtcNow);
        publication.SetOverrides("custom-slug", "Custom title", "Custom description", "SEO title", "SEO description", DateTimeOffset.UtcNow);

        publication.Publish(product, DateTimeOffset.UtcNow);

        Assert.Equal(PublicationState.Published, publication.State);
        Assert.NotNull(publication.PublishedAt);
    }

    [Fact]
    public void Publication_rejects_cross_tenant_assignment()
    {
        var product = NewProduct();

        Assert.Throws<CatalogRuleException>(() =>
            ProductPublication.Assign(Guid.CreateVersion7(), Guid.CreateVersion7(), product, DateTimeOffset.UtcNow));
    }

    private static Product NewProduct()
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
        product.Variants.Add(new Variant
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            Sku = "SKU-1",
            PriceMinor = 1000,
            Currency = "AUD",
            StockQuantity = 10,
            // A shippable product needs item + package weight/dimensions to be publish-ready.
            WeightGrams = 500,
            LengthMm = 200,
            WidthMm = 150,
            HeightMm = 100,
            PackageWeightGrams = 650,
            PackageLengthMm = 250,
            PackageWidthMm = 200,
            PackageHeightMm = 150,
        });
        return product;
    }

    [Fact]
    public void PublicationReadiness_requires_shipping_dimensions_for_a_physical_product()
    {
        var product = NewProduct();
        product.Variants[0].PackageWeightGrams = null; // a physical product missing package dims isn't ready
        var publication = ProductPublication.Assign(product.TenantId, Guid.CreateVersion7(), product, DateTimeOffset.UtcNow);
        publication.SetFulfillment(FulfilmentType.Dropship, "au", "8518", DateTimeOffset.UtcNow);

        var readiness = publication.CheckReadiness(product);

        Assert.False(readiness.IsReady);
        Assert.Contains("product and package weight + dimensions on every shippable variant", readiness.MissingRequirements);
    }

    [Fact]
    public void PublicationReadiness_does_not_require_dimensions_for_a_digital_product()
    {
        var product = NewProduct();
        foreach (var v in product.Variants)
        {
            v.WeightGrams = v.LengthMm = v.WidthMm = v.HeightMm = null;
            v.PackageWeightGrams = v.PackageLengthMm = v.PackageWidthMm = v.PackageHeightMm = null;
        }

        var publication = ProductPublication.Assign(product.TenantId, Guid.CreateVersion7(), product, DateTimeOffset.UtcNow);
        publication.SetFulfillment(FulfilmentType.DigitalDownload, null, null, DateTimeOffset.UtcNow);

        var readiness = publication.CheckReadiness(product);

        Assert.True(readiness.IsReady); // digital ships nothing → no dimension requirement
    }
}
