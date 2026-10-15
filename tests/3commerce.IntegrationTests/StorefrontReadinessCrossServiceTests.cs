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

    // Polls Catalog's read model until the cross-service event has been consumed (or times out). The failure names
    // the last row seen: "no row" = the event never landed; a row with the flag still false = an older event
    // landed after the newer one (the ordering bug AsOf guards against) or the newer one never arrived.
    private async Task WaitForReadinessAsync(Guid storefrontId, Func<Catalog.Domain.StorefrontServiceReadiness, bool> ready)
    {
        Catalog.Domain.StorefrontServiceReadiness? last = null;
        await Eventually.UntilAsync(
            async () =>
            {
                using var scope = fixture.Catalog.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                last = await db.StorefrontServiceReadiness.AsNoTracking().SingleOrDefaultAsync(r => r.StorefrontId == storefrontId);
                return last is not null && ready(last);
            },
            () => new Xunit.Sdk.XunitException(
                $"StorefrontServiceReadiness for {storefrontId} never reached the expected state — last seen: " +
                (last is null
                    ? "no row (the readiness event never reached Catalog)."
                    : $"carrier={last.HasActiveCarrier} payment={last.HasActivePaymentAccount}.")),
            interval: TimeSpan.FromMilliseconds(250));
    }
}
