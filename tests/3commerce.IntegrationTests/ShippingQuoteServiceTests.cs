using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Infrastructure;
using ThreeCommerce.Fulfillment.Infrastructure.Carriers;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_4: quote resolves the tenant's configured carrier, with a Fake fallback.</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class ShippingQuoteServiceTests(Phase4Fixture fixture)
{
    private static readonly ShipAddress Origin = new("Origin", "1 St", "Sydney", "2000", "AU");
    private static readonly ShipAddress Dest = new("Dest", "2 St", "Sydney", "2010", "AU");
    private static readonly RateRequest Request = new(Origin, Dest, new Parcel(1000, 200, 150, 100));

    [Fact]
    public async Task Quote_with_an_empty_parcel_uses_the_default_so_a_quote_always_returns()
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        var quotes = scope.ServiceProvider.GetRequiredService<ShippingQuoteService>();

        var empty = new RateRequest(Origin, Dest, new Parcel(0, 0, 0, 0));
        var withDefault = new RateRequest(Origin, Dest, new Parcel(500, 200, 150, 100));

        var emptyRates = await quotes.QuoteAsync(Guid.NewGuid(), null, empty, default);
        var defaultRates = await quotes.QuoteAsync(Guid.NewGuid(), null, withDefault, default);

        Assert.NotEmpty(emptyRates);
        // The empty parcel resolved to the built-in default (500g), so rates match.
        Assert.Equal(defaultRates.First(r => r.Service == "standard").AmountMinor,
            emptyRates.First(r => r.Service == "standard").AmountMinor);
    }

    [Fact]
    public async Task Quote_falls_back_to_fake_when_no_carrier_is_configured()
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        var quotes = scope.ServiceProvider.GetRequiredService<ShippingQuoteService>();

        var rates = await quotes.QuoteAsync(Guid.NewGuid(), null, Request, default);
        Assert.NotEmpty(rates);
        Assert.All(rates, r => Assert.Equal(CarrierCode.Fake, r.Carrier));
    }

    [Fact]
    public async Task Quote_uses_the_tenants_configured_default_carrier()
    {
        var tenant = Guid.NewGuid();
        using (var setup = fixture.Fulfillment.Services.CreateScope())
        {
            var carriers = setup.ServiceProvider.GetRequiredService<CarrierService>();
            var integration = await carriers.ConfigureAsync(tenant, null, CarrierCode.AustraliaPost, "ap-ref", default);
            await carriers.TransitionAsync(tenant, integration.Id, (c, n) => c.Activate(n), default);
            await carriers.MakeDefaultAsync(tenant, integration.Id, default);
        }

        using var scope = fixture.Fulfillment.Services.CreateScope();
        var quotes = scope.ServiceProvider.GetRequiredService<ShippingQuoteService>();
        var rates = await quotes.QuoteAsync(tenant, null, Request, default);

        Assert.NotEmpty(rates);
        Assert.All(rates, r => Assert.Equal(CarrierCode.AustraliaPost, r.Carrier));
    }

    [Theory]
    [InlineData(CarrierCode.FedEx)]
    [InlineData(CarrierCode.StarTrack)]
    public async Task Quote_uses_an_mt4_10_carrier_when_configured(CarrierCode carrier)
    {
        var tenant = Guid.NewGuid();
        using (var setup = fixture.Fulfillment.Services.CreateScope())
        {
            var carriers = setup.ServiceProvider.GetRequiredService<CarrierService>();
            var integration = await carriers.ConfigureAsync(tenant, null, carrier, "ref", default);
            await carriers.TransitionAsync(tenant, integration.Id, (c, n) => c.Activate(n), default);
            await carriers.MakeDefaultAsync(tenant, integration.Id, default);
        }

        using var scope = fixture.Fulfillment.Services.CreateScope();
        var rates = await scope.ServiceProvider.GetRequiredService<ShippingQuoteService>().QuoteAsync(tenant, null, Request, default);
        Assert.NotEmpty(rates);
        Assert.All(rates, r => Assert.Equal(carrier, r.Carrier));
    }
}
