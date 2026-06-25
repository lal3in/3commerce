using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// FR-12 admin catalog editor (BL-2): create/edit products with variants, stock, images
/// and attributes. The created product is immediately searchable (Products table = FTS source).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class CatalogAdminTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _admin = null!;
    private HttpClient _public = null!;
    private Guid _categoryId;

    public async Task InitializeAsync()
    {
        _catalog = fixture.CreateCatalogFactory();
        _public = _catalog.CreateClient();
        _admin = _catalog.CreateClient();
        _admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin));

        // Products are only searchable under a real category (FTS join) — seed one.
        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        _categoryId = Guid.CreateVersion7();
        // Categories are tenant-scoped (mt3_2); seed under the default tenant the admin
        // create endpoint resolves to, so the category lookup matches.
        db.Categories.Add(new Category
        {
            Id = _categoryId,
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Slug = $"editor-cat-{_categoryId:N}",
            Name = "Editor Test",
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

    private sealed record VariantDto(Guid? Id, string Sku, long PriceMinor, string? Currency, int StockQuantity);
    private sealed record EditorDto(Guid Id, string Slug, string Title, string Brand, string Description,
        Guid CategoryId, Dictionary<string, string> Attributes, List<string> ImageUrls, List<VariantDto> Variants);
    private sealed record HitDto(Guid Id, string Slug, string Title);

    private object WriteBody(string slug, string title, params VariantDto[] variants) => new
    {
        slug,
        title,
        brand = "Acme",
        description = "A test product",
        categoryId = _categoryId,
        attributes = new Dictionary<string, string> { ["color"] = "black" },
        imageUrls = new[] { "https://img.example/1.png" },
        variants,
    };

    [Fact]
    public async Task Create_then_edit_reconciles_variants_and_stock()
    {
        var slug = $"editor-widget-{Guid.NewGuid():N}";
        var create = await _admin.PostAsJsonAsync("/admin/products", WriteBody(slug, "Editor Widget",
            new VariantDto(null, "EW-1", 4999, "EUR", 10),
            new VariantDto(null, "EW-2", 5999, "EUR", 3)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<EditorDto>();
        Assert.Equal(2, created!.Variants.Count);

        // The product is searchable straight away (Products table feeds FTS).
        var hits = await _public.GetFromJsonAsync<List<HitDto>>("/products?q=Editor%20Widget");
        Assert.Contains(hits!, h => h.Id == created.Id);

        // Edit: rename, drop the second variant, restock the first.
        var keep = created.Variants.Single(v => v.Sku == "EW-1");
        var update = await _admin.PutAsJsonAsync($"/admin/products/{created.Id}", WriteBody(slug, "Editor Widget v2",
            new VariantDto(keep.Id, "EW-1", 4499, "EUR", 25)));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var after = await _admin.GetFromJsonAsync<EditorDto>($"/admin/products/{created.Id}");
        Assert.Equal("Editor Widget v2", after!.Title);
        Assert.Single(after.Variants);
        Assert.Equal(25, after.Variants[0].StockQuantity);
        Assert.Equal(4499, after.Variants[0].PriceMinor);
    }

    [Fact]
    public async Task Duplicate_slug_is_rejected()
    {
        var slug = $"dupe-{Guid.NewGuid():N}";
        var first = await _admin.PostAsJsonAsync("/admin/products", WriteBody(slug, "First",
            new VariantDto(null, "D-1", 1000, "EUR", 1)));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _admin.PostAsJsonAsync("/admin/products", WriteBody(slug, "Second",
            new VariantDto(null, "D-2", 1000, "EUR", 1)));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Create_without_variants_is_a_validation_error()
    {
        var resp = await _admin.PostAsJsonAsync("/admin/products", WriteBody($"novariants-{Guid.NewGuid():N}", "No Variants"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Editor_endpoints_require_admin()
    {
        var anon = _catalog.CreateClient();
        var resp = await anon.PostAsJsonAsync("/admin/products", WriteBody("nope", "Nope",
            new VariantDto(null, "N-1", 1000, "EUR", 1)));
        Assert.True(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }
}
