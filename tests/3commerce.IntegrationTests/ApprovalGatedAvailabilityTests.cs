using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Approval-gated storefront availability (DECISION A, strict): a variant is unavailable on a storefront
/// when its only covering offer is from an unapproved supplier — regardless of stock — and becomes
/// available once the supplier is approved. A product with NO offers keeps its catalog behaviour (scope
/// guard). Also proves PR 3's offer-as-price only applies for an approved supplier.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class ApprovalGatedAvailabilityTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _admin = null!;
    private HttpClient _public = null!;
    private Guid _categoryId;
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private sealed record VariantDto(Guid? Id, string Sku, long PriceMinor, string? Currency, int StockQuantity);
    private sealed record EditorDto(Guid Id, string Slug, string Title);
    private sealed record DetailVariantDto(Guid Id, string Sku, long PriceMinor, string Currency, bool InStock);
    private sealed record DetailDto(Guid Id, string Slug, string Title, List<DetailVariantDto> Variants);

    public async Task InitializeAsync()
    {
        _catalog = fixture.CreateCatalogFactory();
        _admin = _catalog.CreateClient();
        _public = _catalog.CreateClient();
        _admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin, tenantId: Tenant.ToString()));

        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        _categoryId = Guid.CreateVersion7();
        db.Categories.Add(new Category { Id = _categoryId, TenantId = Tenant, Slug = $"agv-cat-{_categoryId:N}", Name = "AGV" });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _public.Dispose();
        _catalog.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_variant_is_out_of_stock_while_its_only_offer_is_unapproved_and_available_once_approved()
    {
        var (slug, productId) = await CreateProductAsync(2_000, "EUR");
        var supplierId = Guid.CreateVersion7();

        // A product-level, all-storefront, EUR offer priced at 1500 from a supplier that is NOT yet approved.
        await SeedOfferAsync(productId, supplierId, priceMinor: 1_500, currency: "EUR");

        // Unapproved supplier → the only covering offer does not count → the variant is out of stock (even
        // though StockQuantity is 5), and the offer price is NOT applied (catalog price shown).
        var hidden = await GetDetailAsync(slug);
        var v1 = Assert.Single(hidden.Variants);
        Assert.False(v1.InStock);
        Assert.Equal(2_000, v1.PriceMinor);

        // Approve the supplier → the variant becomes available and the offer-as-price now applies.
        await SetApprovalAsync(supplierId, approved: true);
        var shown = await GetDetailAsync(slug);
        var v2 = Assert.Single(shown.Variants);
        Assert.True(v2.InStock);
        Assert.Equal(1_500, v2.PriceMinor);

        // Revoke again (suspend/archive) → back to unavailable.
        await SetApprovalAsync(supplierId, approved: false);
        var revoked = await GetDetailAsync(slug);
        Assert.False(Assert.Single(revoked.Variants).InStock);
    }

    [Fact]
    public async Task A_product_with_no_offers_keeps_its_catalog_availability()
    {
        // Scope guard: an offerless product has no supplier to gate — it stays available at its catalog price.
        var (slug, _) = await CreateProductAsync(2_000, "EUR");
        var detail = await GetDetailAsync(slug);
        Assert.True(Assert.Single(detail.Variants).InStock);
        Assert.Equal(2_000, Assert.Single(detail.Variants).PriceMinor);
    }

    private async Task<(string Slug, Guid ProductId)> CreateProductAsync(long priceMinor, string currency)
    {
        var slug = $"agv-p-{Guid.NewGuid():N}";
        var body = new
        {
            slug,
            title = "AGV Product",
            brand = "Acme",
            description = "x",
            categoryId = _categoryId,
            attributes = new Dictionary<string, string>(),
            imageUrls = new[] { "https://example.test/img.png" },
            variants = new[] { new VariantDto(null, $"AGV-{Guid.NewGuid():N}"[..12], priceMinor, currency, 5) },
        };
        var resp = await _admin.PostAsJsonAsync("/admin/products", body);
        resp.EnsureSuccessStatusCode();
        return (slug, (await resp.Content.ReadFromJsonAsync<EditorDto>())!.Id);
    }

    // Seeds a product-level, all-storefront offer directly into Catalog's own store (Offer is Catalog-owned).
    private async Task SeedOfferAsync(Guid productId, Guid supplierId, long priceMinor, string currency)
    {
        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var offer = Offer.Create(
            Tenant, productId, variantId: null, supplierId,
            SupplyCategory.Physical, FulfilmentType.Warehouse, priceMinor, currency, priority: 0, DateTimeOffset.UtcNow);
        db.Offers.Add(offer);
        await db.SaveChangesAsync();
    }

    // Stands in for the Entity SupplierApprovalChanged projection by writing the read copy directly.
    private async Task SetApprovalAsync(Guid supplierId, bool approved)
    {
        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleOrDefaultAsync(
            db.SupplierApprovalCopies, s => s.SupplierId == supplierId);
        if (row is null)
        {
            db.SupplierApprovalCopies.Add(new SupplierApprovalCopy { SupplierId = supplierId, TenantId = Tenant, Approved = approved, UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            row.Approved = approved;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    private async Task<DetailDto> GetDetailAsync(string slug)
    {
        var resp = await _public.GetAsync($"/products/{slug}?currency=EUR");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<DetailDto>())!;
    }
}
