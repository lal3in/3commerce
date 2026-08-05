using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_3 / ADR-0042: carrier config lifecycle + per-storefront resolution (no tenant-level default).</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class CarrierServiceTests(Phase4Fixture fixture)
{
    private async Task<T> WithCarrierAsync<T>(Func<CarrierService, Task<T>> work)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<CarrierService>());
    }

    [Fact]
    public async Task Carriers_are_scoped_to_a_storefront_with_no_tenant_fallback()
    {
        var tenant = Guid.NewGuid();
        var storefront = Guid.NewGuid();
        var otherStorefront = Guid.NewGuid();

        // A storefront with no carrier resolves to nothing (there is no tenant-level default).
        Assert.Null(await WithCarrierAsync(s => s.ResolveDefaultAsync(tenant, storefront, default)));
        Assert.False(await WithCarrierAsync(s => s.HasActiveCarrierAsync(tenant, storefront, default)));

        // Configure + activate + default a carrier for the storefront → it resolves for that storefront only.
        var carrier = await WithCarrierAsync(s => s.ConfigureAsync(tenant, storefront, CarrierCode.Dhl, "dhl-ref", default));
        await WithCarrierAsync(s => s.TransitionAsync(tenant, carrier.Id, (c, n) => c.Activate(n), default));
        await WithCarrierAsync(s => s.MakeDefaultAsync(tenant, carrier.Id, default));

        var resolved = await WithCarrierAsync(s => s.ResolveDefaultAsync(tenant, storefront, default));
        Assert.Equal(carrier.Id, resolved!.Id);
        Assert.True(await WithCarrierAsync(s => s.HasActiveCarrierAsync(tenant, storefront, default)));

        // A different storefront still has none — no tenant-level fallback.
        Assert.Null(await WithCarrierAsync(s => s.ResolveDefaultAsync(tenant, otherStorefront, default)));
        Assert.False(await WithCarrierAsync(s => s.HasActiveCarrierAsync(tenant, otherStorefront, default)));
    }

    [Fact]
    public async Task Configuring_a_carrier_without_a_storefront_is_rejected()
    {
        var tenant = Guid.NewGuid();
        await Assert.ThrowsAsync<FulfillmentRuleException>(
            () => WithCarrierAsync(s => s.ConfigureAsync(tenant, Guid.Empty, CarrierCode.Fake, null, default)));
    }

    [Fact]
    public async Task Duplicating_a_storefront_clones_only_that_storefronts_carrier_accounts()
    {
        var tenant = Guid.NewGuid();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var unrelated = Guid.NewGuid();

        // An unrelated storefront's carrier must NOT be cloned.
        await WithCarrierAsync(s => s.ConfigureAsync(tenant, unrelated, CarrierCode.Fake, null, default));

        // Two accounts on the source; make DHL the storefront's active default.
        await WithCarrierAsync(s => s.ConfigureAsync(tenant, source, CarrierCode.AustraliaPost, "ap-ref", default));
        var dhl = await WithCarrierAsync(s => s.ConfigureAsync(tenant, source, CarrierCode.Dhl, "dhl-ref", default));
        await WithCarrierAsync(s => s.TransitionAsync(tenant, dhl.Id, (c, n) => c.Activate(n), default));
        await WithCarrierAsync(s => s.MakeDefaultAsync(tenant, dhl.Id, default));

        var cloned = await WithCarrierAsync(s => s.CloneStorefrontCarriersAsync(tenant, source, target, default));
        Assert.Equal(2, cloned); // only the source storefront's two rows

        var targetCarriers = await WithCarrierAsync(s => s.ListAsync(tenant, target, default));
        Assert.Equal(2, targetCarriers.Count);
        Assert.All(targetCarriers, c => Assert.Equal(target, c.StorefrontId));

        // Config + credential + status + default are carried over.
        var clonedDhl = targetCarriers.Single(c => c.Carrier == CarrierCode.Dhl);
        Assert.Equal("dhl-ref", clonedDhl.CredentialRef);
        Assert.Equal(CarrierIntegrationStatus.Active, clonedDhl.Status);
        Assert.True(clonedDhl.IsDefault);
        var clonedAp = targetCarriers.Single(c => c.Carrier == CarrierCode.AustraliaPost);
        Assert.Equal("ap-ref", clonedAp.CredentialRef);
        Assert.False(clonedAp.IsDefault);

        // The cloned default resolves for the new storefront (its own scope).
        var resolved = await WithCarrierAsync(s => s.ResolveDefaultAsync(tenant, target, default));
        Assert.Equal(CarrierCode.Dhl, resolved!.Carrier);
        Assert.Equal(target, resolved.StorefrontId);

        // Idempotent: a redelivered duplication event clones nothing more.
        Assert.Equal(0, await WithCarrierAsync(s => s.CloneStorefrontCarriersAsync(tenant, source, target, default)));
        Assert.Equal(2, (await WithCarrierAsync(s => s.ListAsync(tenant, target, default))).Count);
    }

    [Fact]
    public async Task MakeDefault_enforces_a_single_default_per_storefront()
    {
        var tenant = Guid.NewGuid();
        var storefront = Guid.NewGuid();
        var first = await WithCarrierAsync(s => s.ConfigureAsync(tenant, storefront, CarrierCode.Fake, null, default));
        var second = await WithCarrierAsync(s => s.ConfigureAsync(tenant, storefront, CarrierCode.AustraliaPost, "ap-ref", default));
        await WithCarrierAsync(s => s.TransitionAsync(tenant, first.Id, (c, n) => c.Activate(n), default));
        await WithCarrierAsync(s => s.TransitionAsync(tenant, second.Id, (c, n) => c.Activate(n), default));

        await WithCarrierAsync(s => s.MakeDefaultAsync(tenant, first.Id, default));
        await WithCarrierAsync(s => s.MakeDefaultAsync(tenant, second.Id, default));

        var defaults = (await WithCarrierAsync(s => s.ListAsync(tenant, storefront, default))).Where(c => c.IsDefault).ToList();
        Assert.Single(defaults);
        Assert.Equal(second.Id, defaults[0].Id);
    }
}
