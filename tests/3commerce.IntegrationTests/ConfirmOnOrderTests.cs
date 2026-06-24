using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_2 unblock: a confirmed order consumes warehouse stock through the bus (uses Order.TenantId).</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class ConfirmOnOrderTests(Phase4Fixture fixture)
{
    [Fact]
    public async Task OrderConfirmed_consumes_warehouse_stock()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var orderId = Guid.CreateVersion7();

        using (var scope = fixture.Fulfillment.Services.CreateScope())
        {
            var inventory = scope.ServiceProvider.GetRequiredService<InventoryService>();
            var location = await inventory.CreateLocationAsync(tenant, Guid.NewGuid(), null, "DC", LocationKind.TenantWarehouse, default);
            await inventory.SetStockAsync(tenant, location.Id, product, null, 10, default);
        }

        var lines = new List<OrderLineInfo>
        {
            new(product, null, "Widget", 3, FulfilmentType.Warehouse, BillingMode.OneTime, 1000),
            new(Guid.NewGuid(), null, "Download", 1, FulfilmentType.DigitalDownload, BillingMode.OneTime, 500),
        };
        await fixture.PublishAsync(new OrderConfirmed(orderId, tenant, "buyer@example.com", 3500, "EUR", lines));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Fulfillment.Services.CreateScope();
            var inventory = scope.ServiceProvider.GetRequiredService<InventoryService>();
            if (await inventory.AvailableAsync(tenant, product, null, default) == 7) // 10 - 3 consumed
            {
                return;
            }

            await Task.Delay(300);
        }

        Assert.Fail("Warehouse stock was not consumed on OrderConfirmed.");
    }
}
