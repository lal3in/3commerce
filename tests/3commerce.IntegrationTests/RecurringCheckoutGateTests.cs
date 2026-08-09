using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Phase 3 verified-member gate: a recurring (subscription) line can only be purchased by a verified,
/// signed-in member with a reusable payment instrument — never as a guest, never unverified, and never
/// without a stored instrument. One-time carts are unaffected.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class RecurringCheckoutGateTests(Phase3Fixture fixture)
{
    private sealed record CheckoutResponseDto(Guid OrderId, string? ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor, long GrossMinor, string Currency, string? Message);

    private static object Checkout(Guid? savedPaymentMethodId = null) => new
    {
        email = "member@example.com",
        shippingAddress = new { name = "M", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
        savedPaymentMethodId,
    };

    private HttpClient MemberClient(bool emailVerified)
    {
        var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName,
            fixture.MintInternalClaims(Guid.NewGuid(), "customer", "member@example.com", emailVerified));
        return client;
    }

    [Fact]
    public async Task A_guest_cannot_purchase_a_subscription()
    {
        var productId = await fixture.SeedRecurringProductAsync(9_000);
        using var guest = fixture.Ordering.CreateClient();
        await guest.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var response = await guest.PostAsJsonAsync("/checkout", Checkout());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Sign in", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unverified_member_cannot_purchase_a_subscription()
    {
        var productId = await fixture.SeedRecurringProductAsync(9_000);
        using var member = MemberClient(emailVerified: false);
        await member.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var response = await member.PostAsJsonAsync("/checkout", Checkout(savedPaymentMethodId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Verify", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_verified_member_needs_a_stored_instrument_for_a_subscription()
    {
        var productId = await fixture.SeedRecurringProductAsync(9_000);
        using var member = MemberClient(emailVerified: true);
        await member.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var response = await member.PostAsJsonAsync("/checkout", Checkout()); // no saved method

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("saved payment method", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_one_time_purchase_is_not_gated_for_a_guest()
    {
        var productId = await fixture.SeedProductAsync(9_000); // one-time (no subscription offer)
        using var guest = fixture.Ordering.CreateClient();
        await guest.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var response = await guest.PostAsJsonAsync("/checkout", Checkout());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CheckoutResponseDto>();
        Assert.NotEqual(Guid.Empty, body!.OrderId);
    }
}
