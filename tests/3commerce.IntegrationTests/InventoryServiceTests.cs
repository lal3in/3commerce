using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_1: inventory locations + the on-hand stock feed against a real Postgres.</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class InventoryServiceTests(Phase4Fixture fixture)
{
    private async Task<T> WithServiceAsync<T>(Func<InventoryService, Task<T>> work)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<InventoryService>());
    }

    [Fact]
    public async Task Stock_feed_is_find_or_create_per_product_variant_location()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var variant = Guid.NewGuid();

        var location = await WithServiceAsync(s =>
            s.CreateLocationAsync(tenant, Guid.NewGuid(), null, "DC1", LocationKind.TenantWarehouse, default));

        // First feed creates the row; second feed for the same tuple updates it (no duplicate).
        await WithServiceAsync(s => s.SetStockAsync(tenant, location.Id, product, variant, 10, default));
        await WithServiceAsync(s => s.SetStockAsync(tenant, location.Id, product, variant, 25, default));
        // A different variant is a distinct row.
        await WithServiceAsync(s => s.SetStockAsync(tenant, location.Id, product, Guid.NewGuid(), 4, default));

        var rows = await WithServiceAsync(s => s.ListStockAsync(tenant, product, default));
        Assert.Equal(2, rows.Count);
        Assert.Equal(25, rows.Single(r => r.VariantId == variant).QuantityOnHand);
    }

    [Fact]
    public async Task Stock_feed_rejects_a_location_from_another_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var location = await WithServiceAsync(s =>
            s.CreateLocationAsync(tenantA, Guid.NewGuid(), null, "DC-A", LocationKind.SupplierDirect, default));

        await Assert.ThrowsAsync<FulfillmentRuleException>(() =>
            WithServiceAsync(s => s.SetStockAsync(tenantB, location.Id, Guid.NewGuid(), null, 5, default)));
    }

    [Fact]
    public async Task Available_sums_active_locations_and_excludes_inactive()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();

        var active = await WithServiceAsync(s =>
            s.CreateLocationAsync(tenant, Guid.NewGuid(), null, "Active", LocationKind.TenantWarehouse, default));
        var inactiveLoc = await WithServiceAsync(s =>
            s.CreateLocationAsync(tenant, Guid.NewGuid(), null, "Inactive", LocationKind.ThirdPartyForwarder, default));

        await WithServiceAsync(s => s.SetStockAsync(tenant, active.Id, product, null, 7, default));
        await WithServiceAsync(s => s.SetStockAsync(tenant, inactiveLoc.Id, product, null, 100, default));

        using (var scope = fixture.Fulfillment.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
            var loc = await db.InventoryLocations.FindAsync(inactiveLoc.Id);
            loc!.Deactivate();
            await db.SaveChangesAsync();
        }

        var available = await WithServiceAsync(s => s.AvailableAsync(tenant, product, null, default));
        Assert.Equal(7, available); // inactive location's 100 is excluded
    }
}
