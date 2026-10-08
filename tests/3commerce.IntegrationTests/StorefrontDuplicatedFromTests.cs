using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Api.Endpoints;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Catalog records which storefront a duplicate was copied from (<c>Storefront.DuplicatedFromStorefrontId</c>): the
/// real duplicate endpoint sets it to the source, a storefront created any other way has none, and no admin update can
/// set or change it (the update request has no such field — a client sending one is ignored). It is exposed read-only
/// on the admin storefront responses and never on the anonymous public config.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class StorefrontDuplicatedFromTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _admin = null!;

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

    private sealed record StorefrontDto(Guid Id, string Name, Guid? DuplicatedFromStorefrontId);

    [Fact]
    public async Task Duplicate_records_its_source_create_records_none_and_no_update_can_change_either()
    {
        var name = $"DupFrom-{Guid.NewGuid():N}"[..24];
        var create = await _admin.PostAsJsonAsync("/admin/storefronts", new { tenantId = TenantId, name, visibility = 1, currency = "EUR" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var source = (await create.Content.ReadFromJsonAsync<StorefrontDto>())!;
        Assert.Null(source.DuplicatedFromStorefrontId);

        var duplicate = await _admin.PostAsJsonAsync($"/admin/storefronts/{source.Id}/duplicate", new { name = $"{name} (copy)" });
        Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);
        var clone = (await duplicate.Content.ReadFromJsonAsync<StorefrontDto>())!;
        Assert.Equal(source.Id, clone.DuplicatedFromStorefrontId);

        // Persisted, not just echoed: the list reads it back from the database.
        Assert.Equal(source.Id, (await ListedAsync(clone.Id)).DuplicatedFromStorefrontId);
        Assert.Null((await ListedAsync(source.Id)).DuplicatedFromStorefrontId);

        // An update carrying the field (a client trying to set / repoint / clear it) changes everything else, never it.
        var bogus = Guid.CreateVersion7();
        var updateClone = await _admin.PutAsJsonAsync($"/admin/storefronts/{clone.Id}", UpdateBody($"{name} renamed", bogus));
        Assert.Equal(HttpStatusCode.OK, updateClone.StatusCode);
        var updatedClone = (await updateClone.Content.ReadFromJsonAsync<StorefrontDto>())!;
        Assert.Equal($"{name} renamed", updatedClone.Name);
        Assert.Equal(source.Id, updatedClone.DuplicatedFromStorefrontId);

        var clearClone = await _admin.PutAsJsonAsync($"/admin/storefronts/{clone.Id}", UpdateBody($"{name} renamed", null));
        Assert.Equal(HttpStatusCode.OK, clearClone.StatusCode);
        Assert.Equal(source.Id, (await ListedAsync(clone.Id)).DuplicatedFromStorefrontId);

        var updateSource = await _admin.PutAsJsonAsync($"/admin/storefronts/{source.Id}", UpdateBody(name, bogus));
        Assert.Equal(HttpStatusCode.OK, updateSource.StatusCode);
        Assert.Null((await ListedAsync(source.Id)).DuplicatedFromStorefrontId);
    }

    [Fact]
    public void The_link_is_admin_only_never_on_the_public_storefront_config()
    {
        Assert.NotNull(typeof(StorefrontResponse).GetProperty(nameof(StorefrontResponse.DuplicatedFromStorefrontId)));
        Assert.Null(typeof(PublicStorefrontResponse).GetProperty("DuplicatedFromStorefrontId"));
        Assert.Null(typeof(CreateStorefrontRequest).GetProperty("DuplicatedFromStorefrontId"));
        Assert.Null(typeof(UpdateStorefrontRequest).GetProperty("DuplicatedFromStorefrontId"));
    }

    private static object UpdateBody(string name, Guid? duplicatedFromStorefrontId) => new
    {
        name,
        visibility = 1,
        currency = "EUR",
        taxRegime = 0,
        taxRateBasisPoints = 0,
        duplicatedFromStorefrontId,
    };

    private async Task<StorefrontDto> ListedAsync(Guid id)
    {
        var list = await _admin.GetFromJsonAsync<List<StorefrontDto>>($"/admin/storefronts?tenantId={TenantId}");
        return Assert.Single(list!, s => s.Id == id);
    }
}
