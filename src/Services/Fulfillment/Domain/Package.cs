using ThreeCommerce.Fulfillment.Domain.Carriers;

namespace ThreeCommerce.Fulfillment.Domain;

public enum PackageStatus { Pending = 1, Labelled = 2, InTransit = 3, Delivered = 4 }

/// <summary>
/// A physical parcel within a shipment (mt4_7). Label purchase + tracking go through the carrier
/// seam; automation is off by default (operators buy labels / refresh tracking manually).
/// </summary>
public sealed class Package
{
    public Guid Id { get; init; }
    public Guid ShipmentId { get; init; }
    public Guid TenantId { get; init; }
    public int WeightGrams { get; init; }
    public int LengthMm { get; init; }
    public int WidthMm { get; init; }
    public int HeightMm { get; init; }
    public CarrierCode? Carrier { get; private set; }
    public string? TrackingNumber { get; private set; }
    public string? LabelUrl { get; private set; }
    public PackageStatus Status { get; private set; } = PackageStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Package() { }

    public static Package Create(Guid shipmentId, Guid tenantId, Parcel parcel, DateTimeOffset now)
    {
        if (shipmentId == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new FulfillmentRuleException("Package shipment and tenant are required.");
        }

        if (parcel.WeightGrams < 0)
        {
            throw new FulfillmentRuleException("Package weight cannot be negative.");
        }

        return new Package
        {
            Id = Guid.CreateVersion7(),
            ShipmentId = shipmentId,
            TenantId = tenantId,
            WeightGrams = parcel.WeightGrams,
            LengthMm = parcel.LengthMm,
            WidthMm = parcel.WidthMm,
            HeightMm = parcel.HeightMm,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Record a purchased label + tracking number (manual or automated, mt4_7).</summary>
    public void ApplyLabel(CarrierLabel label, DateTimeOffset now)
    {
        Carrier = label.Carrier;
        TrackingNumber = label.TrackingNumber;
        LabelUrl = label.LabelUrl;
        Status = PackageStatus.Labelled;
        UpdatedAt = now;
    }

    /// <summary>Apply a polled tracking status (mt4_7).</summary>
    public void ApplyTracking(TrackingStatus tracking, DateTimeOffset now)
    {
        Status = tracking.Status.ToLowerInvariant() switch
        {
            "delivered" => PackageStatus.Delivered,
            "in_transit" => PackageStatus.InTransit,
            _ => Status,
        };
        UpdatedAt = now;
    }
}
