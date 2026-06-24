using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Domain.Dropship;
using ThreeCommerce.Fulfillment.Infrastructure.Dropship;

namespace ThreeCommerce.Fulfillment.Tests;

public class DropshipTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);

    private static SupplierOrderRequest Request() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "buyer@example.com",
            new ShipAddress("N", "1 St", "Sydney", "2000", "AU"),
            [new SupplierOrderItem(Guid.NewGuid(), null, "SKU1", 2)]);

    [Fact]
    public async Task Fake_provider_accepts_and_returns_tracking()
    {
        var result = await new FakeSupplierOrderProvider().SubmitAsync(Request(), default);
        Assert.True(result.Accepted);
        Assert.False(string.IsNullOrWhiteSpace(result.TrackingNumber));
        Assert.Equal("FakeDropshipCarrier", result.Carrier);
    }

    [Fact]
    public void Apply_accepted_with_tracking_moves_to_tracking_received()
    {
        var order = SupplierOrder.Request(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        order.Apply(new SupplierOrderResult(true, "EXT1", "TRACK1", "DHL", null), Now);
        Assert.Equal(SupplierOrderStatus.TrackingReceived, order.Status);
        Assert.Equal("TRACK1", order.TrackingNumber);
    }

    [Fact]
    public void Apply_accepted_without_tracking_is_accepted_only()
    {
        var order = SupplierOrder.Request(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        order.Apply(new SupplierOrderResult(true, "EXT1", null, null, null), Now);
        Assert.Equal(SupplierOrderStatus.Accepted, order.Status);
    }

    [Fact]
    public void Apply_rejected_fails_with_reason()
    {
        var order = SupplierOrder.Request(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        order.Apply(new SupplierOrderResult(false, null, null, null, "out of stock"), Now);
        Assert.Equal(SupplierOrderStatus.Failed, order.Status);
        Assert.Equal("out of stock", order.FailureReason);
    }

    [Fact]
    public void Availability_update_sets_status_and_sellability()
    {
        var item = SupplierAvailability.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Now);
        item.Update(SupplierStockStatus.Available, 50, "SKU1", Now);
        Assert.True(item.IsSellable);
        item.Update(SupplierStockStatus.OutOfStock, 0, null, Now);
        Assert.False(item.IsSellable);
        Assert.Equal("SKU1", item.SupplierSku); // retained when null is passed
    }
}
