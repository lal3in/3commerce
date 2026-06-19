using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Tests;

public class ProductModelTests
{
    [Fact]
    public void ProductModel_requires_tenant_scope()
    {
        var product = NewProduct(Guid.Empty);

        var ex = Assert.Throws<CatalogRuleException>(product.EnsureTenantScoped);

        Assert.Contains("TenantId", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProductIdentifierType.Gtin, " 0123 4567 8901 ", "012345678901")]
    [InlineData(ProductIdentifierType.Mpn, " abc-123 ", "ABC-123")]
    public void ProductModel_normalizes_identifiers(ProductIdentifierType type, string input, string expected)
    {
        var product = NewProduct(Guid.CreateVersion7());

        var identifier = product.AddIdentifier(type, input);

        Assert.Equal(expected, identifier.Value);
    }

    [Fact]
    public void ProductModel_rejects_duplicate_identifier_on_same_product()
    {
        var product = NewProduct(Guid.CreateVersion7());
        product.AddIdentifier(ProductIdentifierType.Ean, "1234567890123");

        Assert.Throws<CatalogRuleException>(() => product.AddIdentifier(ProductIdentifierType.Ean, "1 234567 890123"));
    }

    [Fact]
    public void ProductModel_bundle_components_require_bundle_kind()
    {
        var product = NewProduct(Guid.CreateVersion7());

        Assert.Throws<CatalogRuleException>(() => product.AddBundleComponent(Guid.CreateVersion7(), null, 1));
    }

    [Fact]
    public void ProductModel_bundle_component_cannot_reference_self()
    {
        var product = NewProduct(Guid.CreateVersion7());
        product.Kind = ProductKind.Bundle;

        Assert.Throws<CatalogRuleException>(() => product.AddBundleComponent(product.Id, null, 1));
    }

    [Fact]
    public void ProductModel_bundle_component_tracks_quantity()
    {
        var product = NewProduct(Guid.CreateVersion7());
        product.Kind = ProductKind.Bundle;
        var componentProductId = Guid.CreateVersion7();
        var componentVariantId = Guid.CreateVersion7();

        var component = product.AddBundleComponent(componentProductId, componentVariantId, 2);

        Assert.Equal(componentProductId, component.ComponentProductId);
        Assert.Equal(componentVariantId, component.ComponentVariantId);
        Assert.Equal(2, component.Quantity);
    }

    [Fact]
    public void ProductModel_category_is_tenant_scoped()
    {
        var tenantId = Guid.CreateVersion7();
        var category = new Category { Id = Guid.CreateVersion7(), TenantId = tenantId, Slug = "tools", Name = "Tools" };

        Assert.Equal(tenantId, category.TenantId);
    }

    private static Product NewProduct(Guid tenantId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Slug = "sample",
        Title = "Sample product",
        Brand = "Sample",
        CategoryId = Guid.CreateVersion7(),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
