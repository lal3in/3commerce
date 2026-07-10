using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// mr_9: the public catalog endpoints are a discovery gate — only Active products may be served.
/// An Inactive product must be excluded from public search and return 404 on detail, while staying
/// fully visible/editable through the admin endpoints. (Root cause: the inactive-unpublished-private
/// e2e fixture was seeded as a normal Active product and leaked into public search + home listings.)
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class CatalogVisibilityTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _public = null!;
    private HttpClient _admin = null!;
    private Guid _categoryId;

    private sealed record VariantDto(Guid? Id, string Sku, long PriceMinor, string? Currency, int StockQuantity);
    private sealed record EditorDto(Guid Id, string Slug, string Title, ProductStatus Status, List<VariantDto> Variants);
    private sealed record HitDto(Guid Id, string Slug, string Title, string Brand, long MinPriceMinor, string Currency, string? ImageUrl);
    private sealed record ListItemDto(Guid Id, string Slug, string Title);

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
            Slug = $"visibility-cat-{_categoryId:N}",
            Name = "Visibility Test",
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

    // Enum binds as a NUMBER over HTTP (AGENTS.md invariant): 1=Active, 2=Inactive.
    private object WriteBody(string slug, string title, string marker, int status) => new
    {
        slug,
        title,
        brand = "Acme",
        description = $"A visibility test product {marker}",
        categoryId = _categoryId,
        attributes = new Dictionary<string, string>(),
        imageUrls = Array.Empty<string>(),
        status,
        variants = new[] { new { id = (Guid?)null, sku = $"VIS-{Guid.NewGuid():N}"[..12], priceMinor = 2_000L, currency = "EUR", stockQuantity = 5 } },
    };

    [Fact]
    public async Task Inactive_product_is_hidden_from_public_search_and_detail_but_visible_to_admin()
    {
        var marker = $"Xylophonium{Guid.NewGuid():N}"[..18];
        var slug = $"vis-inactive-{Guid.NewGuid():N}";

        // Seed Active first and prove it IS discoverable, so the gate — not a typo — is what hides it later.
        var create = await _admin.PostAsJsonAsync("/admin/products", WriteBody(slug, $"{marker} Widget", marker, status: 1));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<EditorDto>())!;
        Assert.Equal(ProductStatus.Active, created.Status);

        var activeHits = (await _public.GetFromJsonAsync<List<HitDto>>($"/products?q={marker}"))!;
        Assert.Contains(activeHits, h => h.Slug == slug);
        (await _public.GetAsync($"/products/{slug}")).EnsureSuccessStatusCode();

        // Flip to Inactive via the admin update (status 2).
        var update = await _admin.PutAsJsonAsync($"/admin/products/{created.Id}", WriteBody(slug, $"{marker} Widget", marker, status: 2));
        update.EnsureSuccessStatusCode();
        Assert.Equal(ProductStatus.Inactive, (await update.Content.ReadFromJsonAsync<EditorDto>())!.Status);

        // Public search no longer returns it.
        var inactiveHits = (await _public.GetFromJsonAsync<List<HitDto>>($"/products?q={marker}"))!;
        Assert.DoesNotContain(inactiveHits, h => h.Slug == slug);

        // Public detail is a 404 (treated as non-existent).
        Assert.Equal(HttpStatusCode.NotFound, (await _public.GetAsync($"/products/{slug}")).StatusCode);

        // Admin endpoints stay unfiltered: list + get by id still surface the Inactive product.
        var adminGet = (await _admin.GetFromJsonAsync<EditorDto>($"/admin/products/{created.Id}"))!;
        Assert.Equal(ProductStatus.Inactive, adminGet.Status);
        var adminList = (await _admin.GetFromJsonAsync<List<ListItemDto>>($"/admin/products?q={slug}"))!;
        Assert.Contains(adminList, p => p.Slug == slug);
    }
}
