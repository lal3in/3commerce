using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Storefront admin: adding a domain must persist without a change-tracking crash. A new
/// client-keyed StorefrontDomain added via the tracked storefront's nav collection would be
/// inferred Modified by DetectChanges (UPDATE → 0 rows → DbUpdateConcurrencyException, D2);
/// the endpoint adds it through the context so EF marks it Added. Also asserts the
/// "one canonical domain" rule survives a second canonical add.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class CatalogStorefrontAdminTests(Phase2Fixture fixture) : IAsyncLifetime
{
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

    private sealed record DomainDto(Guid Id, string Host, bool Canonical);
    private sealed record StorefrontDto(Guid Id, string Name, List<DomainDto> Domains);

    [Fact]
    public async Task Add_domain_persists_and_second_canonical_moves_the_flag()
    {
        // Create a fresh storefront under its own tenant to avoid the (TenantId, Name) unique index.
        var create = await _admin.PostAsJsonAsync("/admin/storefronts", new
        {
            tenantId = Guid.CreateVersion7(),
            name = $"SF-{Guid.NewGuid():N}",
            visibility = (int)StorefrontVisibilityValue.Private,
            accessPasswordHash = (string?)null,
            currency = "EUR",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var storefront = await create.Content.ReadFromJsonAsync<StorefrontDto>();
        var id = storefront!.Id;

        // Hosts are globally unique — randomize so parallel tests don't collide.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var host1 = $"a-{suffix}.example.com";
        var host2 = $"b-{suffix}.example.com";

        // The reproduction: this returned 500 (DbUpdateConcurrencyException) before the fix.
        var addFirst = await _admin.PostAsJsonAsync($"/admin/storefronts/{id}/domains", new { host = host1, canonical = true });
        Assert.Equal(HttpStatusCode.OK, addFirst.StatusCode);
        var afterFirst = await addFirst.Content.ReadFromJsonAsync<StorefrontDto>();
        var d1 = Assert.Single(afterFirst!.Domains);
        Assert.Equal(host1, d1.Host);
        Assert.True(d1.Canonical);

        // Adding a second canonical domain flips the first off — exactly one canonical remains.
        var addSecond = await _admin.PostAsJsonAsync($"/admin/storefronts/{id}/domains", new { host = host2, canonical = true });
        Assert.Equal(HttpStatusCode.OK, addSecond.StatusCode);
        var afterSecond = await addSecond.Content.ReadFromJsonAsync<StorefrontDto>();
        Assert.Equal(2, afterSecond!.Domains.Count);
        var canonical = Assert.Single(afterSecond.Domains, d => d.Canonical);
        Assert.Equal(host2, canonical.Host);
        Assert.False(afterSecond.Domains.Single(d => d.Host == host1).Canonical);
    }

    // Mirror of Catalog's StorefrontVisibility ordinals (enums cross HTTP as numbers).
    private enum StorefrontVisibilityValue
    {
        Private = 1,
        Password = 2,
        InviteOnly = 3,
        Public = 4,
    }
}
