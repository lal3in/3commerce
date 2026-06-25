using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Infrastructure.Carriers;

namespace ThreeCommerce.Fulfillment.Infrastructure;

/// <summary>
/// Packages, labels, and tracking for a shipment (mt4_7). Automation is off by default — operators
/// add packages and buy labels / refresh tracking through these calls (manual fallback). Labels and
/// tracking go through the carrier seam (mt4_4); only the Fake provides them today, so it is the
/// fallback for real carriers until their label/tracking adapters land.
/// </summary>
public sealed class ShipmentService(
    FulfillmentDbContext db, TimeProvider clock, CarrierRegistry registry, FakeCarrierProvider fake)
{
    private static readonly ShipAddress Placeholder = new("", "", "", "", "");

    public async Task<Package?> AddPackageAsync(Guid tenantId, Guid shipmentId, Parcel parcel, CancellationToken ct)
    {
        var exists = await db.Shipments.AnyAsync(s => s.Id == shipmentId && s.TenantId == tenantId, ct);
        if (!exists)
        {
            return null;
        }

        var package = Package.Create(shipmentId, tenantId, parcel, clock.GetUtcNow());
        db.Packages.Add(package);
        await db.SaveChangesAsync(ct);
        return package;
    }

    public async Task<Package?> BuyLabelAsync(Guid tenantId, Guid packageId, CarrierCode? carrier, CancellationToken ct)
    {
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == packageId && p.TenantId == tenantId, ct);
        if (package is null)
        {
            return null;
        }

        ICarrierLabelProvider provider = (carrier is { } code ? registry.Labels(code) : null) ?? fake;
        var label = await provider.CreateLabelAsync(
            new LabelRequest("standard", Placeholder, Placeholder,
                new Parcel(package.WeightGrams, package.LengthMm, package.WidthMm, package.HeightMm)), ct);
        package.ApplyLabel(label, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return package;
    }

    public async Task<Package?> RefreshTrackingAsync(Guid tenantId, Guid packageId, CancellationToken ct)
    {
        var package = await db.Packages.FirstOrDefaultAsync(p => p.Id == packageId && p.TenantId == tenantId, ct);
        if (package?.TrackingNumber is null)
        {
            return package;
        }

        ICarrierTrackingProvider provider = (package.Carrier is { } code ? registry.Tracking(code) : null) ?? fake;
        var status = await provider.GetTrackingAsync(package.TrackingNumber, ct);
        package.ApplyTracking(status, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return package;
    }

    public Task<List<Package>> ListPackagesAsync(Guid tenantId, Guid shipmentId, CancellationToken ct) =>
        db.Packages.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.ShipmentId == shipmentId)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
}
