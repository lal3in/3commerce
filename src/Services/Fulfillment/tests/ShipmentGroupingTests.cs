using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Infrastructure.Carriers;

namespace ThreeCommerce.Fulfillment.Tests;

public class ShipmentGroupingTests
{
    private static GroupableLine Line(string source, FulfilmentType type, int qty, int grams) =>
        new(Guid.NewGuid(), null, qty, type, source, new Parcel(grams, 100, 100, 50));

    [Fact]
    public void Groups_by_source_and_excludes_non_shipping_lines()
    {
        var groups = ShipmentGrouping.Group(
        [
            Line("wh:1", FulfilmentType.Warehouse, 1, 500),
            Line("wh:1", FulfilmentType.Warehouse, 2, 300),
            Line("dropship:9", FulfilmentType.Dropship, 1, 1000),
            Line("digital", FulfilmentType.DigitalDownload, 1, 0),
        ]);

        Assert.Equal(2, groups.Count); // wh:1 + dropship:9; digital line doesn't ship
        Assert.Contains(groups, g => g.SourceKey == "wh:1");
        Assert.Contains(groups, g => g.SourceKey == "dropship:9");
    }

    [Fact]
    public void Combined_parcel_sums_weight_across_quantities()
    {
        // 500*1 + 300*2 = 1100
        var group = ShipmentGrouping.Group(
        [
            Line("wh:1", FulfilmentType.Warehouse, 1, 500),
            Line("wh:1", FulfilmentType.Warehouse, 2, 300),
        ]).Single();

        Assert.Equal(1100, group.Parcel.WeightGrams);
    }

    [Fact]
    public void Order_with_only_non_shipping_lines_yields_no_groups() =>
        Assert.Empty(ShipmentGrouping.Group([Line("digital", FulfilmentType.Subscription, 1, 0)]));
}
