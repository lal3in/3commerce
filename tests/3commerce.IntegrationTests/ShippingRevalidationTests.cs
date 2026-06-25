using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Infrastructure;
using ThreeCommerce.Fulfillment.Infrastructure.Carriers;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_6: revalidate a selected quote before payment + carrier fallback.</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class ShippingRevalidationTests(Phase4Fixture fixture)
{
    private static readonly ShipAddress Origin = new("O", "1 St", "Sydney", "2000", "AU");
    private static readonly ShipAddress Dest = new("D", "2 St", "Sydney", "2010", "AU");
    private static RateRequest Request => new(Origin, Dest, new Parcel(1000, 200, 150, 100));

    private async Task<T> WithQuotesAsync<T>(Func<ShippingQuoteService, Task<T>> work)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ShippingQuoteService>());
    }

    [Fact]
    public async Task Revalidate_is_valid_when_price_unchanged_and_not_expired()
    {
        var tenant = Guid.NewGuid();
        var rate = (await WithQuotesAsync(s => s.QuoteAsync(tenant, null, Request, default))).First(r => r.Service == "standard");
        var result = await WithQuotesAsync(s => s.RevalidateAsync(
            tenant, null, Request, "standard", rate.AmountMinor, DateTimeOffset.UtcNow.AddMinutes(10), default));
        Assert.Equal(QuoteRevalidation.Valid, result.Outcome);
    }

    [Fact]
    public async Task Revalidate_is_expired_past_the_expiry()
    {
        var result = await WithQuotesAsync(s => s.RevalidateAsync(
            Guid.NewGuid(), null, Request, "standard", 800, DateTimeOffset.UtcNow.AddMinutes(-1), default));
        Assert.Equal(QuoteRevalidation.Expired, result.Outcome);
    }

    [Fact]
    public async Task Revalidate_is_price_changed_when_amount_differs()
    {
        var result = await WithQuotesAsync(s => s.RevalidateAsync(
            Guid.NewGuid(), null, Request, "standard", 999_999, DateTimeOffset.UtcNow.AddMinutes(10), default));
        Assert.Equal(QuoteRevalidation.PriceChanged, result.Outcome);
        Assert.NotNull(result.CurrentRate);
    }

    [Fact]
    public async Task Revalidate_is_unavailable_when_the_service_is_gone()
    {
        var result = await WithQuotesAsync(s => s.RevalidateAsync(
            Guid.NewGuid(), null, Request, "nonexistent", 800, DateTimeOffset.UtcNow.AddMinutes(10), default));
        Assert.Equal(QuoteRevalidation.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task Quote_falls_back_to_fake_when_the_carrier_has_no_matching_service()
    {
        var tenant = Guid.NewGuid();
        using (var setup = fixture.Fulfillment.Services.CreateScope())
        {
            var carriers = setup.ServiceProvider.GetRequiredService<CarrierService>();
            var integration = await carriers.ConfigureAsync(tenant, null, CarrierCode.AustraliaPost, "ap-ref", default);
            await carriers.TransitionAsync(tenant, integration.Id, (c, n) => c.Activate(n), default);
            await carriers.MakeDefaultAsync(tenant, integration.Id, default);
        }

        // AusPost has no "standard" service → empty → fallback to Fake (which does).
        var rates = await WithQuotesAsync(s => s.QuoteAsync(tenant, null, Request with { Service = "standard" }, default));
        Assert.NotEmpty(rates);
        Assert.All(rates, r => Assert.Equal(CarrierCode.Fake, r.Carrier));
    }
}
