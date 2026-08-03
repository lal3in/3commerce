using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_3: per-tenant product-type shipping policy — which product types require a carrier.</summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class ProductTypePolicyTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _admin = null!;

    private sealed record PolicyRow(string ProductType, int Value, bool RequiresShipping);

    public Task InitializeAsync()
    {
        _catalog = fixture.CreateCatalogFactory();
        _admin = _catalog.CreateClient();
        _admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _catalog.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Default_policy_ships_physical_only_and_updates_persist()
    {
        var tenant = Guid.NewGuid();

        // Default (no row yet): every product type listed, only Physical requires shipping.
        var initial = await _admin.GetFromJsonAsync<List<PolicyRow>>($"/admin/product-types?tenantId={tenant}");
        Assert.Equal(6, initial!.Count);
        Assert.True(initial.Single(r => r.ProductType == "Physical").RequiresShipping);
        Assert.False(initial.Single(r => r.ProductType == "Digital").RequiresShipping);

        // Update: mark Physical + Bundle as shippable.
        var put = await _admin.PutAsJsonAsync("/admin/product-types",
            new { tenantId = tenant, requiresShippingTypes = new[] { "Physical", "Bundle" } });
        put.EnsureSuccessStatusCode();
        var updated = (await put.Content.ReadFromJsonAsync<List<PolicyRow>>())!;
        Assert.True(updated.Single(r => r.ProductType == "Physical").RequiresShipping);
        Assert.True(updated.Single(r => r.ProductType == "Bundle").RequiresShipping);
        Assert.False(updated.Single(r => r.ProductType == "Digital").RequiresShipping);

        // Persisted: a fresh GET reflects the update.
        var reloaded = (await _admin.GetFromJsonAsync<List<PolicyRow>>($"/admin/product-types?tenantId={tenant}"))!;
        Assert.True(reloaded.Single(r => r.ProductType == "Bundle").RequiresShipping);
        Assert.False(reloaded.Single(r => r.ProductType == "Service").RequiresShipping);
    }

    [Fact]
    public async Task Unknown_product_type_is_rejected()
    {
        var response = await _admin.PutAsJsonAsync("/admin/product-types",
            new { tenantId = Guid.NewGuid(), requiresShippingTypes = new[] { "Teleport" } });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
