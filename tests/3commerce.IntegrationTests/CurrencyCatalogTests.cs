using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Per-currency product pricing through the Catalog API (review remediation rev_4 / finding F3):
/// admin round-trip of tenant-set VariantPrice rows, and the public search/detail contract —
/// prices returned in the requested currency, products hidden where the tenant set no price.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class CurrencyCatalogTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _public = null!;
    private HttpClient _admin = null!;
    private Guid _categoryId;

    private sealed record CurrencyPriceDto(string Currency, long PriceMinor);
    private sealed record VariantDto(Guid? Id, string Sku, long PriceMinor, string? Currency, int StockQuantity, List<CurrencyPriceDto>? Prices = null);
    private sealed record EditorDto(Guid Id, string Slug, string Title, string Brand, string Description,
        Guid CategoryId, Dictionary<string, string> Attributes, List<string> ImageUrls, List<VariantDto> Variants);
    private sealed record HitDto(Guid Id, string Slug, string Title, string Brand, long MinPriceMinor, string Currency, string? ImageUrl);
    private sealed record DetailVariantDto(Guid Id, string Sku, long PriceMinor, string Currency, bool InStock);
    private sealed record DetailDto(Guid Id, string Slug, string Title, List<DetailVariantDto> Variants);

    public async Task InitializeAsync()
    {
        _catalog = fixture.CreateCatalogFactory();
        _public = _catalog.CreateClient();
        _admin = _catalog.CreateClient();
        _admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin));

        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        _categoryId = Guid.CreateVersion7();
        db.Categories.Add(new Category
        {
            Id = _categoryId,
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Slug = $"currency-cat-{_categoryId:N}",
            Name = "Currency Test",
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _public.Dispose();
        _catalog.Dispose();
        return Task.CompletedTask;
    }

    private object WriteBody(string slug, string title, params VariantDto[] variants) => new
    {
        slug,
        title,
        brand = "Acme",
        description = "A per-currency test product",
        categoryId = _categoryId,
        attributes = new Dictionary<string, string>(),
        imageUrls = Array.Empty<string>(),
        variants,
    };

    [Fact]
    public async Task Admin_round_trips_and_reconciles_per_currency_prices()
    {
        var slug = $"cur-editor-{Guid.NewGuid():N}";
        var create = await _admin.PostAsJsonAsync("/admin/products", WriteBody(slug, "Currency Editor Widget",
            new VariantDto(null, $"CE-{Guid.NewGuid():N}"[..12], 2_000, "EUR", 5,
                [new("AUD", 3_300), new(" aud ", 9_999), new("usd", 2_160)])));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EditorDto>())!;

        // Normalized: trimmed/upper-cased, first write per currency wins → AUD 3300 (dup dropped), USD 2160.
        var prices = created.Variants.Single().Prices!;
        Assert.Equal(2, prices.Count);
        Assert.Equal(3_300, prices.Single(p => p.Currency == "AUD").PriceMinor);
        Assert.Equal(2_160, prices.Single(p => p.Currency == "USD").PriceMinor);

        // Update: change AUD, drop USD → reconciled, not appended.
        var variant = created.Variants.Single();
        var update = await _admin.PutAsJsonAsync($"/admin/products/{created.Id}", WriteBody(slug, "Currency Editor Widget",
            new VariantDto(variant.Id, variant.Sku, 2_000, "EUR", 5, [new("AUD", 3_500)])));
        update.EnsureSuccessStatusCode();

        var after = (await _admin.GetFromJsonAsync<EditorDto>($"/admin/products/{created.Id}"))!;
        var afterPrices = after.Variants.Single().Prices!;
        Assert.Equal(3_500, Assert.Single(afterPrices).PriceMinor);
    }

    [Fact]
    public async Task Public_detail_prices_in_the_requested_currency_and_hides_unpriced()
    {
        var slug = $"cur-detail-{Guid.NewGuid():N}";
        var create = await _admin.PostAsJsonAsync("/admin/products", WriteBody(slug, "Currency Detail Widget",
            new VariantDto(null, $"CD-{Guid.NewGuid():N}"[..12], 2_000, "EUR", 5, [new("AUD", 3_300)])));
        create.EnsureSuccessStatusCode();

        var aud = (await _public.GetFromJsonAsync<DetailDto>($"/products/{slug}?currency=AUD"))!;
        var audVariant = Assert.Single(aud.Variants);
        Assert.Equal(3_300, audVariant.PriceMinor);
        Assert.Equal("AUD", audVariant.Currency);

        // Base currency still served from the base price.
        var eur = (await _public.GetFromJsonAsync<DetailDto>($"/products/{slug}?currency=EUR"))!;
        Assert.Equal(2_000, Assert.Single(eur.Variants).PriceMinor);

        // Unpriced currency → hidden (404), not shown in a foreign currency.
        var jpy = await _public.GetAsync($"/products/{slug}?currency=JPY");
        Assert.Equal(HttpStatusCode.NotFound, jpy.StatusCode);
    }

    [Fact]
    public async Task Search_prices_hits_in_the_requested_currency_and_hides_unpriced()
    {
        var marker = $"Zephyrion{Guid.NewGuid():N}"[..16];
        var slug = $"cur-search-{Guid.NewGuid():N}";
        var create = await _admin.PostAsJsonAsync("/admin/products", WriteBody(slug, $"{marker} Widget",
            new VariantDto(null, $"CS-{Guid.NewGuid():N}"[..12], 2_000, "EUR", 5, [new("AUD", 3_300)])));
        create.EnsureSuccessStatusCode();

        var aud = (await _public.GetFromJsonAsync<List<HitDto>>($"/products?q={marker}&currency=AUD"))!;
        var hit = Assert.Single(aud);
        Assert.Equal(3_300, hit.MinPriceMinor);
        Assert.Equal("AUD", hit.Currency);

        // No JPY price anywhere on this product → hidden from JPY search entirely.
        var jpy = (await _public.GetFromJsonAsync<List<HitDto>>($"/products?q={marker}&currency=JPY"))!;
        Assert.Empty(jpy);
    }
}
