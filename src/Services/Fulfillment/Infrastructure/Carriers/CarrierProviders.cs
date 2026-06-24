using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Domain.Carriers;

namespace ThreeCommerce.Fulfillment.Infrastructure.Carriers;

/// <summary>
/// Deterministic, keyless carrier for tests/dev (mt4_4) — implements all three seams so UI and CI
/// are unblocked without real credentials. Rates scale with weight; cross-border costs more.
/// </summary>
public sealed class FakeCarrierProvider : ICarrierRateProvider, ICarrierLabelProvider, ICarrierTrackingProvider
{
    public CarrierCode Carrier => CarrierCode.Fake;

    public Task<IReadOnlyList<CarrierRate>> GetRatesAsync(RateRequest request, CancellationToken ct)
    {
        var crossBorder = !string.Equals(request.Origin.Country, request.Destination.Country, StringComparison.OrdinalIgnoreCase);
        var weightUnits = Math.Max(1, request.Parcel.WeightGrams / 500);
        var baseRate = 500 + (weightUnits * 150) + (crossBorder ? 1500 : 0);

        var rates = new List<CarrierRate>
        {
            new(Carrier, "standard", "Fake Standard", baseRate, "AUD", crossBorder ? 10 : 4),
            new(Carrier, "express", "Fake Express", baseRate + 800, "AUD", crossBorder ? 5 : 2),
        };
        return Task.FromResult(Filter(rates, request.Service));
    }

    public Task<CarrierLabel> CreateLabelAsync(LabelRequest request, CancellationToken ct) =>
        Task.FromResult(new CarrierLabel(
            Carrier, "FAKE" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            "https://labels.fake.local/label.pdf"));

    public Task<TrackingStatus> GetTrackingAsync(string trackingNumber, CancellationToken ct) =>
        Task.FromResult(new TrackingStatus(trackingNumber, "in_transit", null));

    internal static IReadOnlyList<CarrierRate> Filter(List<CarrierRate> rates, string? service) =>
        service is null ? rates : rates.Where(r => r.Service == service).ToList();
}

/// <summary>
/// Australia Post rates — sandbox/deterministic placeholder behind the seam (mt4_4). Replace
/// GetRatesAsync with real Shipping &amp; Tracking API calls once credentials are onboarded.
/// </summary>
public sealed class AustraliaPostRateProvider : ICarrierRateProvider
{
    public CarrierCode Carrier => CarrierCode.AustraliaPost;

    public Task<IReadOnlyList<CarrierRate>> GetRatesAsync(RateRequest request, CancellationToken ct)
    {
        var weightUnits = Math.Max(1, request.Parcel.WeightGrams / 500);
        var rates = new List<CarrierRate>
        {
            new(Carrier, "AUS_PARCEL_REGULAR", "Parcel Post", 895 + (weightUnits * 120), "AUD", 5),
            new(Carrier, "AUS_PARCEL_EXPRESS", "Express Post", 1495 + (weightUnits * 180), "AUD", 2),
        };
        return Task.FromResult(FakeCarrierProvider.Filter(rates, request.Service));
    }
}

/// <summary>
/// DHL rates — sandbox/deterministic placeholder behind the seam (mt4_4). Replace GetRatesAsync
/// with real MyDHL API calls once credentials are onboarded.
/// </summary>
public sealed class DhlRateProvider : ICarrierRateProvider
{
    public CarrierCode Carrier => CarrierCode.Dhl;

    public Task<IReadOnlyList<CarrierRate>> GetRatesAsync(RateRequest request, CancellationToken ct)
    {
        var weightUnits = Math.Max(1, request.Parcel.WeightGrams / 500);
        var rates = new List<CarrierRate>
        {
            new(Carrier, "P", "DHL Express Worldwide", 2200 + (weightUnits * 260), "EUR", 4),
        };
        return Task.FromResult(FakeCarrierProvider.Filter(rates, request.Service));
    }
}

/// <summary>Resolves the registered provider for a carrier code (mt4_4). Adapters self-register by Carrier.</summary>
public sealed class CarrierRegistry(
    IEnumerable<ICarrierRateProvider> rateProviders,
    IEnumerable<ICarrierLabelProvider> labelProviders,
    IEnumerable<ICarrierTrackingProvider> trackingProviders)
{
    public ICarrierRateProvider? Rates(CarrierCode code) => rateProviders.FirstOrDefault(p => p.Carrier == code);

    public ICarrierLabelProvider? Labels(CarrierCode code) => labelProviders.FirstOrDefault(p => p.Carrier == code);

    public ICarrierTrackingProvider? Tracking(CarrierCode code) => trackingProviders.FirstOrDefault(p => p.Carrier == code);
}
