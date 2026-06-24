using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_8: manual restock of returned items (direct + via RestockRequested through the bus).</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class RestockTests(Phase4Fixture fixture)
{
    private async Task<Guid> SeedStockAsync(Guid tenant, Guid product, int onHand)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryService>();
        var location = await inventory.CreateLocationAsync(tenant, Guid.NewGuid(), null, "DC", LocationKind.TenantWarehouse, default);
        await inventory.SetStockAsync(tenant, location.Id, product, null, onHand, default);
        return location.Id;
    }

    private async Task<int> AvailableAsync(Guid tenant, Guid product)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InventoryService>().AvailableAsync(tenant, product, null, default);
    }

    [Fact]
    public async Task Restock_increments_on_hand_and_is_idempotent_by_reference()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var rmaId = Guid.NewGuid();
        var locationId = await SeedStockAsync(tenant, product, 5);

        async Task RestockAsync()
        {
            using var scope = fixture.Fulfillment.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ReservationService>()
                .RestockAsync(tenant, rmaId, [new RestockLine(product, null, locationId, 3)], default);
        }

        await RestockAsync();
        Assert.Equal(8, await AvailableAsync(tenant, product));

        await RestockAsync(); // same reference → not applied twice
        Assert.Equal(8, await AvailableAsync(tenant, product));
    }

    [Fact]
    public async Task RestockRequested_event_restocks_through_the_bus()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var locationId = await SeedStockAsync(tenant, product, 2);

        await fixture.PublishAsync(new RestockRequested(tenant, Guid.NewGuid(),
            [new RestockItemInfo(product, null, locationId, 4)]));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await AvailableAsync(tenant, product) == 6) // 2 + 4 restocked
            {
                return;
            }

            await Task.Delay(300);
        }

        Assert.Fail("Restock was not applied from RestockRequested.");
    }
}
