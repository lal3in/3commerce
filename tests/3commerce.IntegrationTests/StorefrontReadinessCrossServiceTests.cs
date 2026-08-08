using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// End-to-end guard for ADR-0042/0043 go-live readiness across service boundaries: driving the REAL
/// Fulfillment carrier and Payments payment-account activation paths (each publishing through its EF bus
/// outbox) must land the readiness signal in Catalog's StorefrontServiceReadiness read model. This is the
/// regression the manual-publish tests miss — if a publisher forgets to flush the outbox (publish without a
/// following SaveChanges), the event is stranded and these assertions fail.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class StorefrontReadinessCrossServiceTests(Phase4Fixture fixture)
{
    private sealed record AccountDto(Guid Id, Guid TenantId, Guid StorefrontId, string Name, string Provider,
        string Mode, string State, bool IsDefaultForStorefront, string? ExternalAccountRef, DateTimeOffset CreatedAt);

    [Fact]
    public async Task Activating_a_carrier_in_fulfillment_projects_readiness_into_catalog()
    {
        var tenant = Guid.NewGuid();
        var storefront = Guid.NewGuid();

        // Real Fulfillment path: configure + activate a carrier via CarrierService (publishes through its outbox).
        using (var scope = fixture.Fulfillment.Services.CreateScope())
        {
            var carriers = scope.ServiceProvider.GetRequiredService<CarrierService>();
            var carrier = await carriers.ConfigureAsync(tenant, storefront, CarrierCode.Dhl, "dhl-ref", default);
            await carriers.TransitionAsync(tenant, carrier.Id, (c, n) => c.Activate(n), default);
        }

        await WaitForReadinessAsync(storefront, r => r.HasActiveCarrier);
    }

    [Fact]
    public async Task Activating_a_payment_account_in_payments_projects_readiness_into_catalog()
    {
        var tenant = Guid.NewGuid();
        var storefront = Guid.NewGuid();

        // Real Payments path: create → submit → activate via the admin HTTP endpoints (publish through the outbox).
        using var admin = fixture.Payments.CreateClient();
        admin.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));

        var created = await (await admin.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = tenant, storefrontId = storefront, name = "Main", provider = "stripe", mode = 1, isDefaultForStorefront = true }))
            .Content.ReadFromJsonAsync<AccountDto>();
        (await admin.PostAsync($"/admin/payment-accounts/{created!.Id}/submit", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/admin/payment-accounts/{created.Id}/activate", null)).EnsureSuccessStatusCode();

        await WaitForReadinessAsync(storefront, r => r.HasActivePaymentAccount);
    }

    // Polls Catalog's read model until the cross-service event has been consumed (or times out).
    private async Task WaitForReadinessAsync(Guid storefrontId, Func<Catalog.Domain.StorefrontServiceReadiness, bool> ready)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Catalog.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var row = await db.StorefrontServiceReadiness.AsNoTracking().SingleOrDefaultAsync(r => r.StorefrontId == storefrontId);
            if (row is not null && ready(row))
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new Xunit.Sdk.XunitException(
            $"StorefrontServiceReadiness for {storefrontId} never reached the expected state — the readiness event was not delivered to Catalog (outbox likely not flushed).");
    }
}
