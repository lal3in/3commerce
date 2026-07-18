using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Per-product ship rules: the tenant feature switch (mandatory per-country rules) gates product
/// create, and rules round-trip through the admin editor DTO.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class ProductShipRuleTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private static readonly Guid DefaultTenant = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _admin = null!;
    private Guid _categoryId;

    public async Task InitializeAsync()
    {
        _catalog = fixture.CreateCatalogFactory();
        _admin = _catalog.CreateClient();
        _admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin));

        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        _categoryId = Guid.CreateVersion7();
        db.Categories.Add(new Category
        {
            Id = _categoryId,
            TenantId = DefaultTenant,
            Slug = $"shiprule-cat-{_categoryId:N}",
            Name = "Ship Rule Test",
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        // Reset the tenant switch so other tests in the collection see the default (off).
        await SetRequireShipRules(false);
        _admin.Dispose();
        _catalog.Dispose();
    }

    private sealed record SettingsDto(Guid TenantId, bool RequireProductShipRules);
    private sealed record ShipRuleDto(string CountryCode, bool ChargeDestinationTax, bool ShippingCovered);
    private sealed record VariantDto(Guid? Id, string Sku, long PriceMinor, string? Currency, int StockQuantity);
    private sealed record EditorDto(Guid Id, string Slug, string Title, List<ShipRuleDto> ShipRules);

    private async Task SetRequireShipRules(bool required)
    {
        var resp = await _admin.PutAsJsonAsync("/admin/settings", new { requireProductShipRules = required });
        resp.EnsureSuccessStatusCode();
    }

    private object WriteBody(string slug, string title, ShipRuleDto[]? shipRules, params VariantDto[] variants) => new
    {
        slug,
        title,
        brand = "Acme",
        description = "A ship-rule product",
        categoryId = _categoryId,
        variants,
        shipRules,
    };

    [Fact]
    public async Task Settings_get_reflects_the_switch_after_put()
    {
        await SetRequireShipRules(true);

        var settings = await _admin.GetFromJsonAsync<SettingsDto>("/admin/settings");

        Assert.NotNull(settings);
        Assert.True(settings!.RequireProductShipRules);
    }

    [Fact]
    public async Task Mandatory_switch_forces_ship_rules_on_create()
    {
        await SetRequireShipRules(true);

        // No rules → 400.
        var slug = $"shiprule-req-{Guid.NewGuid():N}";
        var without = await _admin.PostAsJsonAsync("/admin/products", WriteBody(slug, "Needs Rules", shipRules: null,
            new VariantDto(null, "SR-1", 1999, "EUR", 5)));
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);

        // With a rule → 201, and the rule round-trips.
        var with = await _admin.PostAsJsonAsync("/admin/products", WriteBody(slug, "Needs Rules",
            [new ShipRuleDto("*", false, true)],
            new VariantDto(null, "SR-1", 1999, "EUR", 5)));
        Assert.Equal(HttpStatusCode.Created, with.StatusCode);
        var created = await with.Content.ReadFromJsonAsync<EditorDto>();
        var rule = Assert.Single(created!.ShipRules);
        Assert.Equal("*", rule.CountryCode);
        Assert.False(rule.ChargeDestinationTax);
        Assert.True(rule.ShippingCovered);
    }

    [Fact]
    public async Task Switch_off_allows_create_without_rules()
    {
        await SetRequireShipRules(false);

        var resp = await _admin.PostAsJsonAsync("/admin/products", WriteBody($"shiprule-opt-{Guid.NewGuid():N}", "Optional",
            shipRules: null, new VariantDto(null, "SO-1", 2999, "EUR", 2)));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }
}
