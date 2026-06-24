using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_9: order holds defer fulfilment; releasing the last hold fulfils the captured order.</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class OrderHoldTests(Phase4Fixture fixture)
{
    private static readonly ShipToInfo Ship = new("Buyer", "1 St", "Sydney", "2000", "AU");

    private static OrderConfirmed Confirm(Guid orderId, Guid tenant, Guid product, int qty) =>
        new(orderId, tenant, "buyer@example.com", qty * 1000, "EUR", Ship,
            [new OrderLineInfo(product, null, null, "Widget", qty, FulfilmentType.Warehouse, BillingMode.OneTime, 1000)]);

    private async Task<Guid> SeedStockAsync(Guid tenant, Guid product, int onHand)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryService>();
        var location = await inventory.CreateLocationAsync(tenant, Guid.NewGuid(), null, "DC", LocationKind.TenantWarehouse, default);
        await inventory.SetStockAsync(tenant, location.Id, product, null, onHand, default);
        return location.Id;
    }

    private async Task<bool> HasShipmentAsync(Guid orderId)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>()
            .Shipments.AnyAsync(s => s.OrderId == orderId);
    }

    private async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> done, string failure)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            await Task.Delay(300);
        }

        throw new Xunit.Sdk.XunitException(failure);
    }

    [Fact]
    public async Task Insufficient_stock_auto_holds_then_release_fulfils()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var orderId = Guid.CreateVersion7();

        await fixture.PublishAsync(Confirm(orderId, tenant, product, 2)); // no stock → auto Inventory hold

        var holds = await PollAsync(
            async () =>
            {
                using var scope = fixture.Fulfillment.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<OrderHoldService>().ListAsync(tenant, orderId, default);
            },
            list => list.Any(h => h is { Reason: HoldReason.Inventory, Status: HoldStatus.Active }),
            "No inventory hold was auto-placed.");

        Assert.False(await HasShipmentAsync(orderId)); // held → not fulfilled

        await SeedStockAsync(tenant, product, 5);
        using (var scope = fixture.Fulfillment.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OrderHoldService>()
                .ReleaseHoldAsync(tenant, holds.First(h => h.IsActive).Id, default);
        }

        await PollAsync(() => HasShipmentAsync(orderId), shipped => shipped, "Order was not fulfilled after release.");
    }

    [Fact]
    public async Task Manual_hold_before_confirm_defers_then_release_fulfils()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var orderId = Guid.CreateVersion7();
        await SeedStockAsync(tenant, product, 5); // enough stock → no auto hold

        Guid holdId;
        using (var scope = fixture.Fulfillment.Services.CreateScope())
        {
            var hold = await scope.ServiceProvider.GetRequiredService<OrderHoldService>()
                .PlaceHoldAsync(tenant, orderId, HoldReason.Manual, "fraud review", "operator", default);
            holdId = hold.Id;
        }

        await fixture.PublishAsync(Confirm(orderId, tenant, product, 1));
        await Task.Delay(2000);
        Assert.False(await HasShipmentAsync(orderId)); // manual hold defers

        using (var scope = fixture.Fulfillment.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OrderHoldService>().ReleaseHoldAsync(tenant, holdId, default);
        }

        await PollAsync(() => HasShipmentAsync(orderId), shipped => shipped, "Order was not fulfilled after manual hold release.");
    }
}
