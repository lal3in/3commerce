using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Domain.Dropship;
using ThreeCommerce.Fulfillment.Infrastructure;
using ThreeCommerce.Fulfillment.Infrastructure.Dropship;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_2/mt4_4b unblock: a confirmed order consumes warehouse stock + forwards dropship lines through the bus.</summary>
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
            new(product, null, null, "Widget", 3, FulfilmentType.Warehouse, BillingMode.OneTime, 1000),
            new(Guid.NewGuid(), null, null, "Download", 1, FulfilmentType.DigitalDownload, BillingMode.OneTime, 500),
        };
        var ship = new ShipToInfo("Buyer", "1 St", "Sydney", "2000", "AU");
        await fixture.PublishAsync(new OrderConfirmed(orderId, tenant, "buyer@example.com", 3500, "EUR", ship, lines));

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

    [Fact]
    public async Task OrderConfirmed_forwards_dropship_lines_to_their_supplier()
    {
        var tenant = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        var orderId = Guid.CreateVersion7();

        var lines = new List<OrderLineInfo>
        {
            new(Guid.NewGuid(), null, supplier, "DropItem", 2, FulfilmentType.Dropship, BillingMode.OneTime, 1500),
        };
        var ship = new ShipToInfo("Buyer", "1 St", "Sydney", "2000", "AU");
        await fixture.PublishAsync(new OrderConfirmed(orderId, tenant, "buyer@example.com", 3000, "EUR", ship, lines));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Fulfillment.Services.CreateScope();
            var supplierOrders = scope.ServiceProvider.GetRequiredService<SupplierOrderService>();
            var list = await supplierOrders.ListAsync(tenant, orderId, default);
            if (list.Count == 1 && list[0].SupplierId == supplier && list[0].Status == SupplierOrderStatus.TrackingReceived)
            {
                return;
            }

            await Task.Delay(300);
        }

        Assert.Fail("Dropship line was not forwarded to the supplier on OrderConfirmed.");
    }
}
