using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Regression: the Catalog offer-create endpoint must reject an EXACT-key duplicate. An offer is
/// uniquely identified by (TenantId, ProductId, VariantId, SupplierId, StorefrontId). A non-idempotent
/// caller (a re-run seed) previously piled many rows onto one key, so the Admin Suppliers cost table
/// listed the same product/variant twice. This asserts the guard rejects the same key while STILL
/// allowing a different supplier (multi-supplier) and a different storefront (per-storefront pricing).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class CatalogOfferDuplicateGuardTests(Phase2Fixture fixture)
{
    private HttpClient AdminClient()
    {
        var catalog = fixture.CreateCatalogFactory();
        var admin = catalog.CreateClient();
        admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin));
        return admin;
    }

    private static object OfferBody(Guid productId, Guid supplierId, Guid? variantId, Guid? storefrontId) => new
    {
        productId,
        variantId,
        supplierId,
        storefrontId,
        supplyCategory = (int)SupplyCategory.Physical,
        fulfilmentType = (int)FulfilmentType.Warehouse,
        priceMinor = 1599L,
        supplierCostMinor = 800L,
        currency = "EUR",
        priority = 10,
    };

    [Fact]
    public async Task Creating_an_offer_with_the_same_full_key_is_rejected()
    {
        var admin = AdminClient();
        var productId = Guid.CreateVersion7();
        var supplierId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        var first = await admin.PostAsJsonAsync("/admin/offers", OfferBody(productId, supplierId, variantId, null));
        first.EnsureSuccessStatusCode();

        var second = await admin.PostAsJsonAsync("/admin/offers", OfferBody(productId, supplierId, variantId, null));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Contains("already exists", await second.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_different_supplier_on_the_same_product_still_creates()
    {
        var admin = AdminClient();
        var productId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        var first = await admin.PostAsJsonAsync(
            "/admin/offers", OfferBody(productId, Guid.CreateVersion7(), variantId, null));
        first.EnsureSuccessStatusCode();

        // Multi-supplier: another supplier covering the same product/variant is NOT a duplicate.
        var second = await admin.PostAsJsonAsync(
            "/admin/offers", OfferBody(productId, Guid.CreateVersion7(), variantId, null));
        second.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_same_supplier_with_a_different_storefront_still_creates()
    {
        var admin = AdminClient();
        var productId = Guid.CreateVersion7();
        var supplierId = Guid.CreateVersion7();
        var variantId = Guid.CreateVersion7();

        var first = await admin.PostAsJsonAsync(
            "/admin/offers", OfferBody(productId, supplierId, variantId, Guid.CreateVersion7()));
        first.EnsureSuccessStatusCode();

        // Per-storefront pricing: the same supplier with a DIFFERENT StorefrontId is a distinct offer.
        var second = await admin.PostAsJsonAsync(
            "/admin/offers", OfferBody(productId, supplierId, variantId, Guid.CreateVersion7()));
        second.EnsureSuccessStatusCode();
    }
}
