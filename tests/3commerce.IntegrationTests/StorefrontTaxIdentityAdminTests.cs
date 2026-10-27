using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0063 P2 through the real Catalog admin API: a storefront's TaxJurisdiction + SellerEstablishmentKey are
/// validated (400 problem+json naming the field), persisted, carried on StorefrontConfigChanged, kept by an
/// update that does not send them (older client), audited, and cloned by POST /{id}/duplicate.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class StorefrontTaxIdentityAdminTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
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

    private sealed record StorefrontDto(Guid Id, string Name, string TaxJurisdiction, string SellerEstablishmentKey, Guid? DuplicatedFromStorefrontId);

    [Theory]
    [InlineData("Australia", "sydney-hq", "TaxJurisdiction")]
    [InlineData("AU", "Sydney HQ", "SellerEstablishmentKey")]
    public async Task Invalid_tax_identity_is_a_400_problem_naming_the_field(string jurisdiction, string key, string field)
    {
        var response = await _admin.PostAsJsonAsync("/admin/storefronts", Create($"Bad-{Guid.NewGuid():N}"[..20], jurisdiction, key));

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"expected 400, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(JsonDocument.Parse(raw).RootElement.GetProperty("errors").TryGetProperty(field, out _), $"errors does not name '{field}': {raw}");
    }

    [Fact]
    public async Task Tax_identity_is_published_kept_by_older_clients_audited_and_cloned_on_duplication()
    {
        var configs = new ConcurrentQueue<StorefrontConfigChanged>();
        var audited = new ConcurrentQueue<AuditEntryRecorded>();
        var probe = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(new Uri(fixture.RabbitMqUri));
            cfg.ReceiveEndpoint($"sf-tax-identity-probe-{Guid.NewGuid():N}", e =>
            {
                e.AutoDelete = true;
                e.Durable = false;
                e.Handler<StorefrontConfigChanged>(ctx =>
                {
                    if (ctx.Message.TenantId == _tenantId)
                    {
                        configs.Enqueue(ctx.Message);
                    }

                    return Task.CompletedTask;
                });
                e.Handler<AuditEntryRecorded>(ctx =>
                {
                    audited.Enqueue(ctx.Message);
                    return Task.CompletedTask;
                });
            });
        });
        await probe.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        try
        {
            var name = $"Tax-{Guid.NewGuid():N}"[..20];
            var created = await _admin.PostAsJsonAsync("/admin/storefronts", Create(name, "us-ca", "LA-Office"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var storefront = (await created.Content.ReadFromJsonAsync<StorefrontDto>())!;
            Assert.Equal(("US-CA", "la-office"), (storefront.TaxJurisdiction, storefront.SellerEstablishmentKey));
            await WaitForAsync(configs, c => c.StorefrontId == storefront.Id && c.TaxJurisdiction == "US-CA" && c.SellerEstablishmentKey == "la-office", "the create StorefrontConfigChanged");

            // An update that does not send the fields (an older admin client) keeps them.
            var update = await _admin.PutAsJsonAsync($"/admin/storefronts/{storefront.Id}", new
            {
                name,
                visibility = 1,
                accessPasswordHash = (string?)null,
                publicUrl = (string?)null,
                currency = "USD",
                taxRegime = 3,
                taxRateBasisPoints = 725,
            });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            Assert.Equal("US-CA", (await update.Content.ReadFromJsonAsync<StorefrontDto>())!.TaxJurisdiction);
            await WaitForAsync(configs, c => c.StorefrontId == storefront.Id && c.TaxRateBasisPoints == 725 && c.TaxJurisdiction == "US-CA", "the update StorefrontConfigChanged");

            // Clearing with "" is explicit.
            var cleared = await _admin.PutAsJsonAsync($"/admin/storefronts/{storefront.Id}", new
            {
                name,
                visibility = 1,
                accessPasswordHash = (string?)null,
                publicUrl = (string?)null,
                currency = "USD",
                taxRegime = 3,
                taxRateBasisPoints = 725,
                taxJurisdiction = "US-NY",
                sellerEstablishmentKey = "",
            });
            var afterClear = (await cleared.Content.ReadFromJsonAsync<StorefrontDto>())!;
            Assert.Equal(("US-NY", ""), (afterClear.TaxJurisdiction, afterClear.SellerEstablishmentKey));

            // Duplication clones the identity onto the copy and publishes it for the copy.
            var duplicate = await _admin.PostAsJsonAsync($"/admin/storefronts/{storefront.Id}/duplicate", new { name = $"{name} copy" });
            Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);
            var clone = (await duplicate.Content.ReadFromJsonAsync<StorefrontDto>())!;
            Assert.Equal(("US-NY", storefront.Id), (clone.TaxJurisdiction, clone.DuplicatedFromStorefrontId));
            await WaitForAsync(configs, c => c.StorefrontId == clone.Id && c.TaxJurisdiction == "US-NY", "the duplicate's StorefrontConfigChanged");

            // Every accepted mutation is audited (create, update ×2, duplicate).
            foreach (var (action, resource) in new[]
                     {
                         ("catalog.storefront.create", storefront.Id), ("catalog.storefront.update", storefront.Id),
                         ("catalog.storefront.duplicate", clone.Id),
                     })
            {
                await WaitForAsync(audited, a => a.Action == action && a.ResourceId == resource.ToString(), action);
            }
        }
        finally
        {
            await probe.StopAsync();
        }
    }

    private object Create(string name, string jurisdiction, string key) => new
    {
        tenantId = _tenantId,
        name,
        visibility = 1, // Private
        accessPasswordHash = (string?)null,
        currency = "USD",
        taxRegime = 3, // UsSalesTax
        taxRateBasisPoints = 725,
        taxJurisdiction = jurisdiction,
        sellerEstablishmentKey = key,
    };

    // Awake-time budget + a final poll (Eventually, #309) — never a wall-clock deadline.
    private static async Task<T> WaitForAsync<T>(ConcurrentQueue<T> queue, Func<T, bool> match, string what)
        where T : class
    {
        T? hit = null;
        await Eventually.UntilAsync(
            () => Task.FromResult((hit = queue.FirstOrDefault(match)) is not null),
            () => new TimeoutException($"Timed out waiting for {what}."),
            TimeSpan.FromSeconds(60));
        return hit!;
    }
}
