using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain.Carriers;

namespace ThreeCommerce.Fulfillment.Infrastructure.Carriers;

/// <summary>A line to be shipped, tagged with the origin/source it ships from and its per-unit parcel.</summary>
public sealed record GroupableLine(
    Guid ProductId, Guid? VariantId, int Quantity, FulfilmentType FulfilmentType, string SourceKey, Parcel Parcel);

/// <summary>One shipment group (lines that ship together from one source) with its combined parcel.</summary>
public sealed record ShipmentGroup(
    string SourceKey, FulfilmentType FulfilmentType, IReadOnlyList<GroupableLine> Lines, Parcel Parcel);

/// <summary>
/// Splits an order's lines into shipment groups (mt4_5): physical lines ship, grouped by their
/// source (warehouse location / dropship supplier); digital/service lines don't ship. Each group
/// gets a combined parcel so it can be quoted independently — one order, multiple shipments.
/// </summary>
public static class ShipmentGrouping
{
    private static bool Ships(FulfilmentType type) => type is FulfilmentType.Warehouse or FulfilmentType.Dropship;

    public static IReadOnlyList<ShipmentGroup> Group(IEnumerable<GroupableLine> lines) =>
        lines.Where(l => Ships(l.FulfilmentType))
            .GroupBy(l => l.SourceKey)
            .Select(g => new ShipmentGroup(
                g.Key,
                g.First().FulfilmentType,
                g.ToList(),
                Combine(g.SelectMany(l => Enumerable.Repeat(l.Parcel, l.Quantity)))))
            .ToList();

    /// <summary>
    /// Naive combined parcel for a quote estimate: total weight, parcels stacked (heights summed,
    /// max width/length). Carrier-accurate packing is out of scope; this is a deterministic estimate.
    /// </summary>
    public static Parcel Combine(IEnumerable<Parcel> parcels)
    {
        var list = parcels.ToList();
        return list.Count == 0
            ? new Parcel(0, 0, 0, 0)
            : new Parcel(
                list.Sum(p => p.WeightGrams),
                list.Max(p => p.LengthMm),
                list.Max(p => p.WidthMm),
                list.Sum(p => p.HeightMm));
    }
}
