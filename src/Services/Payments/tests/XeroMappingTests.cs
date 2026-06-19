using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Domain.Xero;

namespace ThreeCommerce.Payments.Tests;

public class XeroMappingTests
{
    private readonly XeroMappingResolver _resolver = new();

    [Fact]
    public void Xero_mapping_uses_tenant_default_when_no_override_matches()
    {
        var tenantId = Guid.CreateVersion7();
        var mappings = new[] { Mapping(tenantId, XeroMappingScope.TenantDefault, Accounts.RevenueSales, "200") };

        var code = _resolver.Resolve(Accounts.RevenueSales, new XeroMappingContext(tenantId), mappings);

        Assert.Equal("200", code);
    }

    [Fact]
    public void Xero_mapping_prefers_storefront_over_tenant_default()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var mappings = new[]
        {
            Mapping(tenantId, XeroMappingScope.TenantDefault, Accounts.RevenueSales, "200"),
            Mapping(tenantId, XeroMappingScope.Storefront, Accounts.RevenueSales, "201", storefrontId: storefrontId),
        };

        var code = _resolver.Resolve(Accounts.RevenueSales, new XeroMappingContext(tenantId, StorefrontId: storefrontId), mappings);

        Assert.Equal("201", code);
    }

    [Fact]
    public void Xero_mapping_prefers_product_over_supplier_category_storefront_defaults()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();
        var supplierId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var mappings = new[]
        {
            Mapping(tenantId, XeroMappingScope.TenantDefault, Accounts.RevenueSales, "200"),
            Mapping(tenantId, XeroMappingScope.Storefront, Accounts.RevenueSales, "201", storefrontId: storefrontId),
            Mapping(tenantId, XeroMappingScope.Category, Accounts.RevenueSales, "202", categoryId: categoryId),
            Mapping(tenantId, XeroMappingScope.Supplier, Accounts.RevenueSales, "203", supplierId: supplierId),
            Mapping(tenantId, XeroMappingScope.Product, Accounts.RevenueSales, "204", productId: productId),
        };

        var code = _resolver.Resolve(Accounts.RevenueSales, new XeroMappingContext(tenantId, storefrontId, categoryId, supplierId, productId), mappings);

        Assert.Equal("204", code);
    }

    [Fact]
    public void Xero_mapping_rejects_unmapped_account()
    {
        Assert.Throws<XeroMappingRuleException>(() =>
            _resolver.Resolve(Accounts.RevenueSales, new XeroMappingContext(Guid.CreateVersion7()), []));
    }

    private static XeroAccountMapping Mapping(
        Guid tenantId,
        XeroMappingScope scope,
        string ledgerCode,
        string xeroCode,
        Guid? storefrontId = null,
        Guid? categoryId = null,
        Guid? supplierId = null,
        Guid? productId = null) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Scope = scope,
            LedgerAccountCode = ledgerCode,
            XeroAccountCode = xeroCode,
            StorefrontId = storefrontId,
            CategoryId = categoryId,
            SupplierEntityId = supplierId,
            ProductId = productId,
        };
}
