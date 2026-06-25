using System.Net.Http.Json;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_5: the Postman-testable shipping quote endpoint (single + per-group).</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class ShippingQuoteEndpointTests(Phase4Fixture fixture)
{
    private static readonly object Origin = new { name = "O", line1 = "1 St", city = "Sydney", postcode = "2000", country = "AU" };
    private static readonly object Dest = new { name = "D", line1 = "2 St", city = "Sydney", postcode = "2010", country = "AU" };
    private static readonly object Parcel = new { weightGrams = 1000, lengthMm = 200, widthMm = 150, heightMm = 100 };

    private sealed record RateDto(string Carrier, string Service, long AmountMinor, string Currency);
    private sealed record QuoteResponseDto(List<RateDto> Rates);
    private sealed record GroupQuoteDto(string SourceKey, List<RateDto> Rates);
    private sealed record GroupQuoteResponseDto(List<GroupQuoteDto> Groups);

    [Fact]
    public async Task Quote_endpoint_returns_rates_anonymously()
    {
        using var client = fixture.Fulfillment.CreateClient();
        var response = await client.PostAsJsonAsync("/shipping/quote",
            new { origin = Origin, destination = Dest, parcel = Parcel });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<QuoteResponseDto>();
        Assert.NotEmpty(result!.Rates);
        Assert.Contains(result.Rates, r => r.Service == "standard");
    }

    [Fact]
    public async Task Group_quote_endpoint_returns_a_quote_per_group()
    {
        using var client = fixture.Fulfillment.CreateClient();
        var response = await client.PostAsJsonAsync("/shipping/quote/groups", new
        {
            destination = Dest,
            groups = new[]
            {
                new { sourceKey = "wh:1", origin = Origin, parcel = Parcel },
                new { sourceKey = "dropship:9", origin = Origin, parcel = Parcel },
            },
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<GroupQuoteResponseDto>();
        Assert.Equal(2, result!.Groups.Count);
        Assert.All(result.Groups, g => Assert.NotEmpty(g.Rates));
        Assert.Contains(result.Groups, g => g.SourceKey == "wh:1");
    }
}
