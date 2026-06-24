using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_7: add a package to a shipment, buy a label, refresh tracking (manual, automation off).</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class ShipmentPackageTests(Phase4Fixture fixture)
{
    private async Task<T> WithShipmentsAsync<T>(Func<ShipmentService, Task<T>> work)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ShipmentService>());
    }

    private async Task<Guid> SeedShipmentAsync(Guid tenant)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
        var shipment = new Shipment
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            OrderId = Guid.NewGuid(),
            FulfillmentSource = "Warehouse",
            Status = ShipmentStatus.Created,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();
        return shipment.Id;
    }

    [Fact]
    public async Task Add_package_then_buy_label_then_refresh_tracking()
    {
        var tenant = Guid.NewGuid();
        var shipmentId = await SeedShipmentAsync(tenant);

        var package = await WithShipmentsAsync(s => s.AddPackageAsync(tenant, shipmentId, new Parcel(1000, 200, 150, 100), default));
        Assert.NotNull(package);
        Assert.Equal(PackageStatus.Pending, package!.Status);

        var labelled = await WithShipmentsAsync(s => s.BuyLabelAsync(tenant, package.Id, null, default));
        Assert.Equal(PackageStatus.Labelled, labelled!.Status);
        Assert.False(string.IsNullOrWhiteSpace(labelled.TrackingNumber));
        Assert.Equal(CarrierCode.Fake, labelled.Carrier);

        var tracked = await WithShipmentsAsync(s => s.RefreshTrackingAsync(tenant, package.Id, default));
        Assert.Equal(PackageStatus.InTransit, tracked!.Status); // Fake tracking reports in_transit
    }

    [Fact]
    public async Task Add_package_to_an_unknown_shipment_returns_null()
    {
        var result = await WithShipmentsAsync(s =>
            s.AddPackageAsync(Guid.NewGuid(), Guid.NewGuid(), new Parcel(1, 1, 1, 1), default));
        Assert.Null(result);
    }
}
