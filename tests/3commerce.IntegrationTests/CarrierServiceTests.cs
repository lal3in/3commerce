using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_3: carrier config lifecycle + tenant-default / storefront-override resolution.</summary>
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
    public async Task Storefront_default_overrides_the_tenant_default()
    {
        var tenant = Guid.NewGuid();
        var storefront = Guid.NewGuid();

        // Tenant-level default (Fake, active).
        var tenantCarrier = await WithCarrierAsync(s => s.ConfigureAsync(tenant, null, CarrierCode.Fake, null, default));
        await WithCarrierAsync(s => s.TransitionAsync(tenant, tenantCarrier.Id, (c, n) => c.Activate(n), default));
        await WithCarrierAsync(s => s.MakeDefaultAsync(tenant, tenantCarrier.Id, default));

        // With no storefront override, the storefront resolves to the tenant default.
        var resolved = await WithCarrierAsync(s => s.ResolveDefaultAsync(tenant, storefront, default));
        Assert.Equal(tenantCarrier.Id, resolved!.Id);

        // Add a storefront-scoped default (DHL) → it now wins for that storefront.
        var storefrontCarrier = await WithCarrierAsync(s => s.ConfigureAsync(tenant, storefront, CarrierCode.Dhl, "dhl-ref", default));
        await WithCarrierAsync(s => s.TransitionAsync(tenant, storefrontCarrier.Id, (c, n) => c.Activate(n), default));
        await WithCarrierAsync(s => s.MakeDefaultAsync(tenant, storefrontCarrier.Id, default));

        var overridden = await WithCarrierAsync(s => s.ResolveDefaultAsync(tenant, storefront, default));
        Assert.Equal(storefrontCarrier.Id, overridden!.Id);

        // A different storefront with no override still falls back to the tenant default.
        var other = await WithCarrierAsync(s => s.ResolveDefaultAsync(tenant, Guid.NewGuid(), default));
        Assert.Equal(tenantCarrier.Id, other!.Id);
    }

    [Fact]
    public async Task Duplicating_a_storefront_clones_its_carrier_accounts_but_not_tenant_defaults()
    {
        var tenant = Guid.NewGuid();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();

        // A tenant-level default (applies to all storefronts via resolution — must NOT be cloned).
        await WithCarrierAsync(s => s.ConfigureAsync(tenant, null, CarrierCode.Fake, null, default));

        // Two storefront-scoped accounts on the source; make DHL the storefront's active default.
        var ap = await WithCarrierAsync(s => s.ConfigureAsync(tenant, source, CarrierCode.AustraliaPost, "ap-ref", default));
        var dhl = await WithCarrierAsync(s => s.ConfigureAsync(tenant, source, CarrierCode.Dhl, "dhl-ref", default));
        await WithCarrierAsync(s => s.TransitionAsync(tenant, dhl.Id, (c, n) => c.Activate(n), default));
        await WithCarrierAsync(s => s.MakeDefaultAsync(tenant, dhl.Id, default));

        var cloned = await WithCarrierAsync(s => s.CloneStorefrontCarriersAsync(tenant, source, target, default));
        Assert.Equal(2, cloned); // only the two storefront-scoped rows, not the tenant default

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

        // The cloned default resolves for the new storefront (it has its own scope).
        var resolved = await WithCarrierAsync(s => s.ResolveDefaultAsync(tenant, target, default));
        Assert.NotNull(resolved);
        Assert.Equal(CarrierCode.Dhl, resolved!.Carrier);
        Assert.Equal(target, resolved.StorefrontId);

        // Idempotent: a redelivered duplication event clones nothing more.
        var again = await WithCarrierAsync(s => s.CloneStorefrontCarriersAsync(tenant, source, target, default));
        Assert.Equal(0, again);
        Assert.Equal(2, (await WithCarrierAsync(s => s.ListAsync(tenant, target, default))).Count);
    }

    [Fact]
    public async Task MakeDefault_enforces_a_single_default_per_scope()
    {
        var tenant = Guid.NewGuid();
        var first = await WithCarrierAsync(s => s.ConfigureAsync(tenant, null, CarrierCode.Fake, null, default));
        var second = await WithCarrierAsync(s => s.ConfigureAsync(tenant, null, CarrierCode.AustraliaPost, "ap-ref", default));
        await WithCarrierAsync(s => s.TransitionAsync(tenant, first.Id, (c, n) => c.Activate(n), default));
        await WithCarrierAsync(s => s.TransitionAsync(tenant, second.Id, (c, n) => c.Activate(n), default));

        await WithCarrierAsync(s => s.MakeDefaultAsync(tenant, first.Id, default));
        await WithCarrierAsync(s => s.MakeDefaultAsync(tenant, second.Id, default));

        var defaults = (await WithCarrierAsync(s => s.ListAsync(tenant, null, default))).Where(c => c.IsDefault).ToList();
        Assert.Single(defaults);
        Assert.Equal(second.Id, defaults[0].Id);
    }
}
