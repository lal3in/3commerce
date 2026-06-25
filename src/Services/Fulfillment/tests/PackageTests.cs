using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Domain.Carriers;

namespace ThreeCommerce.Fulfillment.Tests;

public class PackageTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);

    private static Package New() =>
        Package.Create(Guid.NewGuid(), Guid.NewGuid(), new Parcel(1000, 200, 150, 100), Now);

    [Fact]
    public void Create_starts_pending_without_a_label()
    {
        var package = New();
        Assert.Equal(PackageStatus.Pending, package.Status);
        Assert.Null(package.TrackingNumber);
        Assert.Null(package.Carrier);
    }

    [Fact]
    public void ApplyLabel_records_carrier_tracking_and_marks_labelled()
    {
        var package = New();
        package.ApplyLabel(new CarrierLabel(CarrierCode.Fake, "TRACK1", "https://label"), Now);
        Assert.Equal(PackageStatus.Labelled, package.Status);
        Assert.Equal("TRACK1", package.TrackingNumber);
        Assert.Equal(CarrierCode.Fake, package.Carrier);
    }

    [Theory]
    [InlineData("in_transit", PackageStatus.InTransit)]
    [InlineData("delivered", PackageStatus.Delivered)]
    public void ApplyTracking_maps_known_statuses(string status, PackageStatus expected)
    {
        var package = New();
        package.ApplyLabel(new CarrierLabel(CarrierCode.Fake, "T", "u"), Now);
        package.ApplyTracking(new TrackingStatus("T", status, null), Now);
        Assert.Equal(expected, package.Status);
    }

    [Fact]
    public void ApplyTracking_unknown_status_leaves_state_unchanged()
    {
        var package = New();
        package.ApplyLabel(new CarrierLabel(CarrierCode.Fake, "T", "u"), Now);
        package.ApplyTracking(new TrackingStatus("T", "weird", null), Now);
        Assert.Equal(PackageStatus.Labelled, package.Status);
    }
}
