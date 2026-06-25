using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt7_6: the customer "my access" surface is scoped to the signed-in customer's email claim.</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class DigitalMeTests(Phase4Fixture fixture)
{
    private static readonly ShipToInfo Ship = new("Buyer", "1 St", "Sydney", "2000", "AU");

    private sealed record MeEntitlement(Guid Id, Guid OrderId, string CustomerEmail, string Type, string Status);

    private HttpClient Client(string? email, Guid tenant)
    {
        var client = fixture.Entitlement.CreateClient();
        client.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.NewGuid(), "customer", email, tenant.ToString()));
        return client;
    }

    [Fact]
    public async Task Me_entitlements_returns_only_the_signed_in_customers_access()
    {
        var tenant = Guid.NewGuid();
        const string email = "me@example.com";
        var orderId = Guid.CreateVersion7();

        await fixture.PublishAsync(new OrderConfirmed(orderId, tenant, email, 1500, "EUR", Ship, new List<OrderLineInfo>
        {
            new(Guid.NewGuid(), null, null, "E-book", 1, FulfilmentType.DigitalDownload, BillingMode.OneTime, 1500),
        }));

        // The signed-in customer sees their entitlement once the digital line is fulfilled.
        using var mine = Client(email, tenant);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        List<MeEntitlement>? list;
        while (true)
        {
            list = await mine.GetFromJsonAsync<List<MeEntitlement>>("/me/entitlements");
            if (list!.Count == 1 || DateTimeOffset.UtcNow > deadline)
            {
                break;
            }

            await Task.Delay(300);
        }

        Assert.Single(list!);
        Assert.Equal(email, list![0].CustomerEmail);
        Assert.Equal("Download", list[0].Type);

        // A different customer in the same tenant sees nothing.
        using var other = Client("someone-else@example.com", tenant);
        Assert.Empty((await other.GetFromJsonAsync<List<MeEntitlement>>("/me/entitlements"))!);

        // No email claim → unauthorized (we never scope by a client-supplied value).
        using var anon = Client(email: null, tenant);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/me/entitlements")).StatusCode);
    }
}
