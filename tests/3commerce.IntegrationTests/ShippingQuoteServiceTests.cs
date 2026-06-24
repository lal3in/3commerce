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
}
