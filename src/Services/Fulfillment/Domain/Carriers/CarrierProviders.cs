namespace ThreeCommerce.Fulfillment.Domain.Carriers;

/// <summary>A parcel's shippable dimensions (carrier rate inputs, mt4_11).</summary>
public sealed record Parcel(int WeightGrams, int LengthMm, int WidthMm, int HeightMm);

public sealed record ShipAddress(string Name, string Line1, string City, string Postcode, string Country);

public sealed record RateRequest(ShipAddress Origin, ShipAddress Destination, Parcel Parcel, string? Service = null);

/// <summary>One quoted shipping option from a carrier.</summary>
public sealed record CarrierRate(
    CarrierCode Carrier, string Service, string ServiceName, long AmountMinor, string Currency, int EstimatedDays);

public sealed record LabelRequest(string Service, ShipAddress Origin, ShipAddress Destination, Parcel Parcel);

public sealed record CarrierLabel(CarrierCode Carrier, string TrackingNumber, string LabelUrl);

public sealed record TrackingStatus(string TrackingNumber, string Status, DateTimeOffset? DeliveredAt);

/// <summary>Rate seam (mt4_4): quote shipping options for a parcel. One implementation per carrier.</summary>
public interface ICarrierRateProvider
{
    public CarrierCode Carrier { get; }

    public Task<IReadOnlyList<CarrierRate>> GetRatesAsync(RateRequest request, CancellationToken ct);
}

/// <summary>Label seam (mt4_4/mt4_7): buy a label + tracking number. Fake-only until carriers onboard.</summary>
public interface ICarrierLabelProvider
{
    public CarrierCode Carrier { get; }

    public Task<CarrierLabel> CreateLabelAsync(LabelRequest request, CancellationToken ct);
}

/// <summary>Tracking seam (mt4_4/mt4_7): poll/parse a tracking number's status.</summary>
public interface ICarrierTrackingProvider
{
    public CarrierCode Carrier { get; }

    public Task<TrackingStatus> GetTrackingAsync(string trackingNumber, CancellationToken ct);
}
