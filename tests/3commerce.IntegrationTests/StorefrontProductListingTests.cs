using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// The storefront↔product listing endpoints: a storefront's products (with per-product currency
/// readiness) and a product's storefronts. "Currency ready" = every variant is priced in the store's
/// currency (base currency match or an explicit per-currency price) — otherwise the product would be
/// hidden there (amber in the admin).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class StorefrontProductListingTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _admin = null!;
    private Guid _categoryId;
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private sealed record CurrencyPriceDto(string Currency, long PriceMinor);
    private sealed record VariantDto(Guid? Id, string Sku, long PriceMinor, string? Currency, int StockQuantity, List<CurrencyPriceDto>? Prices = null);
    private sealed record EditorDto(Guid Id, string Slug, string Title);
    private sealed record StorefrontDto(Guid Id, string Name);
    private sealed record StorefrontProductRow(Guid ProductId, string Title, string State, string StorefrontCurrency, bool CurrencyReady);
    private sealed record ProductStorefrontRow(Guid StorefrontId, string StorefrontName, string Currency, string StorefrontState, string PublicationState, bool CurrencyReady);

    public async Task InitializeAsync()
    {
        _catalog = fixture.CreateCatalogFactory();
        _admin = _catalog.CreateClient();
        _admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin, tenantId: Tenant.ToString()));

        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        _categoryId = Guid.CreateVersion7();
        db.Categories.Add(new Category { Id = _categoryId, TenantId = Tenant, Slug = $"sf-cat-{_categoryId:N}", Name = "SF Test" });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _catalog.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> CreateProductAsync(string baseCurrency, List<CurrencyPriceDto>? extra = null)
    {
        var body = new
        {
            slug = $"sf-p-{Guid.NewGuid():N}",
            title = "SF Product",
            brand = "Acme",
            description = "x",
            categoryId = _categoryId,
            attributes = new Dictionary<string, string>(),
            imageUrls = new[] { "https://example.test/img.png" }, // publishing requires an image
            variants = new[] { new VariantDto(null, $"SF-{Guid.NewGuid():N}"[..12], 2_000, baseCurrency, 5, extra) },
        };
        var resp = await _admin.PostAsJsonAsync("/admin/products", body);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<EditorDto>())!.Id;
    }

    private async Task<Guid> CreateStorefrontAsync(string currency)
    {
        var resp = await _admin.PostAsJsonAsync("/admin/storefronts", new
        {
            tenantId = Tenant,
            name = $"SF-{Guid.NewGuid():N}",
            visibility = 1,
            currency,
        });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<StorefrontDto>())!.Id;
    }

    [Fact]
    public async Task Storefront_products_flag_currency_readiness_and_enable_state()
    {
        var storefrontId = await CreateStorefrontAsync("EUR");
        var pricedEur = await CreateProductAsync("EUR");                                    // base = store currency → ready
        var unpricedForEur = await CreateProductAsync("USD");                               // no EUR price → NOT ready (amber)
        var explicitEur = await CreateProductAsync("USD", [new CurrencyPriceDto("EUR", 1_800)]); // explicit EUR price → ready

        foreach (var pid in new[] { pricedEur, unpricedForEur, explicitEur })
        {
            (await _admin.PostAsJsonAsync($"/admin/storefronts/{storefrontId}/products", new { productId = pid, fulfillmentSource = 2, countryOfOrigin = "AU" })).EnsureSuccessStatusCode();
        }

        var rows = await _admin.GetFromJsonAsync<List<StorefrontProductRow>>($"/admin/storefronts/{storefrontId}/products");
        Assert.Equal(3, rows!.Count);
        Assert.True(rows.Single(r => r.ProductId == pricedEur).CurrencyReady);
        Assert.False(rows.Single(r => r.ProductId == unpricedForEur).CurrencyReady);   // amber
        Assert.True(rows.Single(r => r.ProductId == explicitEur).CurrencyReady);
        Assert.All(rows, r => Assert.Equal("EUR", r.StorefrontCurrency));
        // Assigned but not yet enabled → Draft.
        Assert.All(rows, r => Assert.Equal("Draft", r.State));

        // Enable (publish) one — it flips to Published.
        (await _admin.PostAsync($"/admin/storefronts/{storefrontId}/products/{pricedEur}/publish", null)).EnsureSuccessStatusCode();
        var afterPublish = await _admin.GetFromJsonAsync<List<StorefrontProductRow>>($"/admin/storefronts/{storefrontId}/products");
        Assert.Equal("Published", afterPublish!.Single(r => r.ProductId == pricedEur).State);
    }

    [Fact]
    public async Task Product_storefronts_flag_currency_readiness()
    {
        var eurStore = await CreateStorefrontAsync("EUR");
        var usdStore = await CreateStorefrontAsync("USD");
        var product = await CreateProductAsync("EUR"); // priced in EUR only

        (await _admin.PostAsJsonAsync($"/admin/storefronts/{eurStore}/products", new { productId = product, fulfillmentSource = 2, countryOfOrigin = "AU" })).EnsureSuccessStatusCode();
        (await _admin.PostAsJsonAsync($"/admin/storefronts/{usdStore}/products", new { productId = product, fulfillmentSource = 2, countryOfOrigin = "AU" })).EnsureSuccessStatusCode();

        var rows = await _admin.GetFromJsonAsync<List<ProductStorefrontRow>>($"/admin/products/{product}/storefronts");
        Assert.Equal(2, rows!.Count);
        Assert.True(rows.Single(r => r.Currency == "EUR").CurrencyReady);   // priced in EUR
        Assert.False(rows.Single(r => r.Currency == "USD").CurrencyReady);  // no USD price → amber
    }
}
