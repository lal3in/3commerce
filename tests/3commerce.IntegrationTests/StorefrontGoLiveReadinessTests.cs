using System.Net;
using System.Net.Http.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0042 go-live gate: a storefront can't be activated without an active payment account (and an
/// active carrier when it lists physical products). Carrier/payment presence is projected into Catalog
/// via StorefrontCarrierReadinessChanged / StorefrontPaymentReadinessChanged.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class StorefrontGoLiveReadinessTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _admin = null!;
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private sealed record StorefrontDto(Guid Id, string Name, int State);

    public Task InitializeAsync()
    {
        _catalog = fixture.CreateCatalogFactory();
        _admin = _catalog.CreateClient();
        _admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin, tenantId: Tenant.ToString()));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _catalog.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Storefront_cannot_go_live_without_an_active_payment_account()
    {
        // A storefront that satisfies domain + visibility but has no payment account.
        var create = await _admin.PostAsJsonAsync("/admin/storefronts", new
        {
            tenantId = Tenant,
            name = $"GoLive-{Guid.NewGuid():N}",
            visibility = 4, // Public
            publicUrl = "http://localhost:3000/golive", // required to go live (ledger_sf review #4)
            currency = "EUR",
            taxRegime = 2, // EU VAT — a tax regime is required to go live
            taxRateBasisPoints = 2000,
        });
        create.EnsureSuccessStatusCode();
        var storefront = (await create.Content.ReadFromJsonAsync<StorefrontDto>())!;
        var host = $"go-live-{storefront.Id:N}.test";
        (await _admin.PostAsJsonAsync($"/admin/storefronts/{storefront.Id}/domains", new { host, canonical = true })).EnsureSuccessStatusCode();
        (await _admin.PostAsync($"/admin/storefronts/{storefront.Id}/preview", null)).EnsureSuccessStatusCode();

        // No payment account projected yet → activation is blocked.
        var blocked = await _admin.PostAsync($"/admin/storefronts/{storefront.Id}/activate", null);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("active payment account", await blocked.Content.ReadAsStringAsync());

        // Project a carrier signal only — still blocked (payment account is the missing piece).
        await PublishAsync(new StorefrontCarrierReadinessChanged(Tenant, storefront.Id, true));
        await WaitForReadinessAsync(storefront.Id, r => r.HasActiveCarrier);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsync($"/admin/storefronts/{storefront.Id}/activate", null)).StatusCode);

        // Project an active payment account → the store (no published physical products) can now go live.
        await PublishAsync(new StorefrontPaymentReadinessChanged(Tenant, storefront.Id, true));
        await WaitForReadinessAsync(storefront.Id, r => r.HasActivePaymentAccount);

        var activated = await _admin.PostAsync($"/admin/storefronts/{storefront.Id}/activate", null);
        activated.EnsureSuccessStatusCode();
    }

    private async Task PublishAsync<T>(T message) where T : class
    {
        using var scope = _catalog.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await bus.Publish(message);
        await db.SaveChangesAsync(); // flush the transactional outbox so the consumer runs
    }

    private async Task WaitForReadinessAsync(Guid storefrontId, Func<Catalog.Domain.StorefrontServiceReadiness, bool> ready)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = _catalog.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var row = await db.StorefrontServiceReadiness.AsNoTracking().SingleOrDefaultAsync(r => r.StorefrontId == storefrontId);
            if (row is not null && ready(row))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException($"StorefrontServiceReadiness for {storefrontId} never reached the expected state.");
    }
}
