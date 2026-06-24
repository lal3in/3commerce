using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Domain.Dropship;
using ThreeCommerce.Fulfillment.Infrastructure.Dropship;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_4b: forward dropship orders to a supplier + the supplier availability feed.</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class DropshipServiceTests(Phase4Fixture fixture)
{
    private async Task<T> WithOrdersAsync<T>(Func<SupplierOrderService, Task<T>> work)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<SupplierOrderService>());
    }

    private async Task<T> WithAvailabilityAsync<T>(Func<SupplierAvailabilityService, Task<T>> work)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<SupplierAvailabilityService>());
    }

    [Fact]
    public async Task Forward_creates_a_supplier_order_with_tracking_and_is_idempotent()
    {
        var tenant = Guid.NewGuid();
        var request = new SupplierOrderRequest(Guid.NewGuid(), Guid.NewGuid(), "buyer@example.com",
            new ShipAddress("N", "1 St", "Sydney", "2000", "AU"),
            [new SupplierOrderItem(Guid.NewGuid(), null, "SKU", 1)]);

        var first = await WithOrdersAsync(s => s.ForwardAsync(tenant, request, default));
        var second = await WithOrdersAsync(s => s.ForwardAsync(tenant, request, default));

        Assert.Equal(first.Id, second.Id); // idempotent per (order, supplier)
        Assert.Equal(SupplierOrderStatus.TrackingReceived, first.Status);
        Assert.False(string.IsNullOrWhiteSpace(first.TrackingNumber));

        var listed = await WithOrdersAsync(s => s.ListAsync(tenant, request.OrderId, default));
        Assert.Single(listed);
    }

    [Fact]
    public async Task Availability_feed_is_find_or_create_and_overwrites_status()
    {
        var tenant = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        var product = Guid.NewGuid();

        await WithAvailabilityAsync(s => s.SetAsync(tenant, supplier, product, null, SupplierStockStatus.Available, 50, "SKU", default));
        await WithAvailabilityAsync(s => s.SetAsync(tenant, supplier, product, null, SupplierStockStatus.OutOfStock, 0, "SKU", default));

        var list = await WithAvailabilityAsync(s => s.ListAsync(tenant, supplier, product, default));
        Assert.Single(list);
        Assert.Equal(SupplierStockStatus.OutOfStock, list[0].Status);
        Assert.False(list[0].IsSellable);
    }
}
