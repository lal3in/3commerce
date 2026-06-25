using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Infrastructure.Carriers;

namespace ThreeCommerce.Fulfillment.Tests;

public class CarrierAdapterTests
{
    private static readonly ShipAddress Au = new("A", "1 St", "Sydney", "2000", "AU");
    private static readonly ShipAddress De = new("B", "2 St", "Berlin", "10115", "DE");

    private static RateRequest Domestic(int grams) => new(Au, Au, new Parcel(grams, 200, 150, 100));
    private static RateRequest International(int grams) => new(Au, De, new Parcel(grams, 200, 150, 100));

    [Fact]
    public async Task Fake_returns_two_services_and_scales_with_weight()
    {
        var fake = new FakeCarrierProvider();
        var light = await fake.GetRatesAsync(Domestic(300), default);
        var heavy = await fake.GetRatesAsync(Domestic(5000), default);

        Assert.Equal(2, light.Count);
        Assert.All(light, r => Assert.Equal(CarrierCode.Fake, r.Carrier));
        Assert.True(heavy[0].AmountMinor > light[0].AmountMinor);
        Assert.Contains(light, r => r.Service == "express" && r.AmountMinor > light.First(s => s.Service == "standard").AmountMinor);
    }

    [Fact]
    public async Task Fake_cross_border_costs_more_than_domestic()
    {
        var fake = new FakeCarrierProvider();
        var domestic = (await fake.GetRatesAsync(Domestic(1000), default))[0].AmountMinor;
        var international = (await fake.GetRatesAsync(International(1000), default))[0].AmountMinor;
        Assert.True(international > domestic);
    }

    [Fact]
    public async Task Fake_service_filter_narrows_results()
    {
        var fake = new FakeCarrierProvider();
        var rates = await fake.GetRatesAsync(Domestic(1000) with { Service = "express" }, default);
        Assert.Single(rates);
        Assert.Equal("express", rates[0].Service);
    }

    [Fact]
    public async Task Fake_label_and_tracking_round_trip()
    {
        var fake = new FakeCarrierProvider();
        var label = await fake.CreateLabelAsync(new LabelRequest("standard", Au, Au, new Parcel(1000, 1, 1, 1)), default);
        Assert.Equal(CarrierCode.Fake, label.Carrier);
        Assert.StartsWith("FAKE", label.TrackingNumber);
        var tracking = await fake.GetTrackingAsync(label.TrackingNumber, default);
        Assert.Equal(label.TrackingNumber, tracking.TrackingNumber);
    }

    [Fact]
    public async Task AustraliaPost_and_Dhl_return_rates_for_their_carrier()
    {
        var ausPost = await new AustraliaPostRateProvider().GetRatesAsync(Domestic(1000), default);
        Assert.NotEmpty(ausPost);
        Assert.All(ausPost, r => Assert.Equal(CarrierCode.AustraliaPost, r.Carrier));

        var dhl = await new DhlRateProvider().GetRatesAsync(International(1000), default);
        Assert.NotEmpty(dhl);
        Assert.All(dhl, r => Assert.Equal(CarrierCode.Dhl, r.Carrier));
        Assert.Equal("EUR", dhl[0].Currency);
    }

    [Fact]
    public void Registry_resolves_a_provider_per_carrier_code()
    {
        var fake = new FakeCarrierProvider();
        var registry = new CarrierRegistry(
            [fake, new AustraliaPostRateProvider(), new DhlRateProvider()],
            [fake],
            [fake]);

        Assert.Equal(CarrierCode.AustraliaPost, registry.Rates(CarrierCode.AustraliaPost)!.Carrier);
        Assert.Equal(CarrierCode.Dhl, registry.Rates(CarrierCode.Dhl)!.Carrier);
        Assert.Equal(CarrierCode.Fake, registry.Rates(CarrierCode.Fake)!.Carrier);
        Assert.Null(registry.Rates(CarrierCode.Ups)); // not registered in this minimal set
        Assert.NotNull(registry.Labels(CarrierCode.Fake));
        Assert.NotNull(registry.Tracking(CarrierCode.Fake));
    }

    [Theory]
    [InlineData("USD")]
    public async Task FedEx_and_Ups_quote_in_usd(string currency)
    {
        foreach (ICarrierRateProvider provider in new ICarrierRateProvider[] { new FedExRateProvider(), new UpsRateProvider() })
        {
            var rates = await provider.GetRatesAsync(International(1000), default);
            Assert.NotEmpty(rates);
            Assert.All(rates, r => Assert.Equal(provider.Carrier, r.Carrier));
            Assert.All(rates, r => Assert.Equal(currency, r.Currency));
        }
    }

    [Fact]
    public async Task StarTrack_and_PackAndSend_quote_in_aud()
    {
        foreach (ICarrierRateProvider provider in new ICarrierRateProvider[] { new StarTrackRateProvider(), new PackAndSendRateProvider() })
        {
            var rates = await provider.GetRatesAsync(Domestic(1000), default);
            Assert.NotEmpty(rates);
            Assert.All(rates, r => Assert.Equal(provider.Carrier, r.Carrier));
            Assert.All(rates, r => Assert.Equal("AUD", r.Currency));
        }
    }

    [Fact]
    public void Registry_resolves_all_six_real_carriers_plus_fake()
    {
        var fake = new FakeCarrierProvider();
        var registry = new CarrierRegistry(
            [
                fake, new AustraliaPostRateProvider(), new DhlRateProvider(),
                new FedExRateProvider(), new UpsRateProvider(), new StarTrackRateProvider(), new PackAndSendRateProvider(),
            ],
            [fake], [fake]);

        foreach (var code in new[]
        {
            CarrierCode.Fake, CarrierCode.AustraliaPost, CarrierCode.Dhl,
            CarrierCode.FedEx, CarrierCode.Ups, CarrierCode.StarTrack, CarrierCode.PackAndSend,
        })
        {
            Assert.Equal(code, registry.Rates(code)!.Carrier);
        }
    }
}
