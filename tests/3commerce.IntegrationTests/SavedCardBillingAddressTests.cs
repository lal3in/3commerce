using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// mem_2 — each saved payment method carries its own billing address, over Payments' real HTTP surface and
/// Postgres: a card saved with an address lists it (normalized); PUT replaces and DELETE clears it per card without
/// touching the member's other card; a malformed address is 400 problem+json naming the field and saves nothing;
/// another member gets 404 for the card (no existence leak) and cannot change it; no claims → 401. The address is
/// prefill data only — nothing here creates a charge, and the card's charge snapshot is unchanged (unit lane).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class SavedCardBillingAddressTests(Phase3Fixture fixture)
{
    private static readonly object Melbourne =
        new { name = " Ada Buyer ", line1 = "1 Test Street", city = "Melbourne", region = "VIC", postcode = "3000", country = "au" };

    [Fact]
    public async Task Each_card_keeps_its_own_billing_address()
    {
        using var client = fixture.Payments.CreateClient();
        var member = NewMember();
        var visa = await SaveCardAsync(client, member, "pm_card_visa_4242_1229", Melbourne);
        var amex = await SaveCardAsync(client, member, "pm_card_amex_0005_0130", billingAddress: null);

        var cards = await ListAsync(client, member);
        AssertMelbourne(Card(cards, visa).GetProperty("billingAddress"));
        Assert.Equal(JsonValueKind.Null, Card(cards, amex).GetProperty("billingAddress").ValueKind);

        // Replace the Amex's address only.
        var put = await SendAsync(client, member, HttpMethod.Put, $"/payment-methods/{amex}/billing-address",
            new { name = "Acme Accounts", line1 = "9 Ledger Road", city = "Munich", postcode = "80331", country = "DE" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("DE", (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("billingAddress").GetProperty("country").GetString());

        cards = await ListAsync(client, member);
        AssertMelbourne(Card(cards, visa).GetProperty("billingAddress"));
        Assert.Equal("Munich", Card(cards, amex).GetProperty("billingAddress").GetProperty("city").GetString());

        // Clear the Visa's: the Amex keeps its own.
        Assert.Equal(HttpStatusCode.NoContent,
            (await SendAsync(client, member, HttpMethod.Delete, $"/payment-methods/{visa}/billing-address")).StatusCode);
        cards = await ListAsync(client, member);
        Assert.Equal(JsonValueKind.Null, Card(cards, visa).GetProperty("billingAddress").ValueKind);
        Assert.Equal("Munich", Card(cards, amex).GetProperty("billingAddress").GetProperty("city").GetString());

        // Stored at microsecond precision (the UpdatedAt the domain set is the one Postgres returns).
        await using var scope = fixture.Payments.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var stored = await db.SavedPaymentMethods.AsNoTracking().SingleAsync(m => m.Id == amex);
        Assert.Equal(0, stored.UpdatedAt.Ticks % TimeSpan.TicksPerMicrosecond);
    }

    [Theory]
    [InlineData("""{"name":"","line1":"1 St","city":"Melbourne","postcode":"3000","country":"AU"}""", "name")]
    [InlineData("""{"name":"Ada","line1":"1 St","city":"Melbourne","postcode":"3000","country":"AUS"}""", "country")]
    [InlineData("""{"name":"Ada","line1":"1 St","city":"","postcode":"3000","country":"AU"}""", "city")]
    [InlineData("""{"name":"Ada","line1":"1 St","city":"Melbourne","postcode":"","country":"AU"}""", "postcode")]
    public async Task A_malformed_address_is_a_400_naming_the_field_and_saves_nothing(string body, string field)
    {
        using var client = fixture.Payments.CreateClient();
        var member = NewMember();
        var card = await SaveCardAsync(client, member, "pm_card_visa_4242_1229", Melbourne);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/payment-methods/{card}/billing-address")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(InternalClaimsAuth.HeaderName, member.Claims);
        var refused = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(field, problem.GetProperty("field").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"errors has no '{field}': {problem}");
        AssertMelbourne(Card(await ListAsync(client, member), card).GetProperty("billingAddress"));
    }

    [Fact]
    public async Task A_card_with_a_malformed_address_is_not_saved()
    {
        using var client = fixture.Payments.CreateClient();
        var member = NewMember();

        var refused = await SendAsync(client, member, HttpMethod.Post, "/payment-methods/", new
        {
            providerPaymentMethodId = "pm_card_visa_4242_1229",
            email = member.Email,
            billingAddress = new { name = "Ada", line1 = "1 St", city = "Melbourne", postcode = "3000", country = "Australia" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("billingAddress.country", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Equal(0, (await ListAsync(client, member)).GetArrayLength());
    }

    [Fact]
    public async Task Only_the_cards_owner_can_change_its_address()
    {
        using var client = fixture.Payments.CreateClient();
        var owner = NewMember();
        var stranger = NewMember();
        var card = await SaveCardAsync(client, owner, "pm_card_visa_4242_1229", Melbourne);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, stranger, HttpMethod.Put, $"/payment-methods/{card}/billing-address",
            new { name = "Mallory", line1 = "6 Evil St", city = "Nowhere", postcode = "0000", country = "US" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(client, stranger, HttpMethod.Delete, $"/payment-methods/{card}/billing-address")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, owner, HttpMethod.Put, $"/payment-methods/{Guid.CreateVersion7()}/billing-address",
            new { name = "Ada", line1 = "1 St", city = "Melbourne", postcode = "3000", country = "AU" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"/payment-methods/{card}/billing-address")).StatusCode);

        AssertMelbourne(Card(await ListAsync(client, owner), card).GetProperty("billingAddress"));
    }

    private sealed record Member(Guid UserId, string Email, string Claims);

    private Member NewMember()
    {
        var userId = Guid.CreateVersion7();
        var email = $"card-billing-{userId:N}@example.com";
        return new Member(userId, email, fixture.MintInternalClaims(userId, "customer", email, emailVerified: true));
    }

    private static void AssertMelbourne(JsonElement address)
    {
        Assert.Equal("Ada Buyer", address.GetProperty("name").GetString());
        Assert.Equal("1 Test Street", address.GetProperty("line1").GetString());
        Assert.Equal("Melbourne", address.GetProperty("city").GetString());
        Assert.Equal("VIC", address.GetProperty("region").GetString());
        Assert.Equal("3000", address.GetProperty("postcode").GetString());
        Assert.Equal("AU", address.GetProperty("country").GetString());
    }

    private static JsonElement Card(JsonElement cards, Guid id) =>
        cards.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id);

    private static async Task<Guid> SaveCardAsync(HttpClient client, Member member, string token, object? billingAddress)
    {
        var saved = await SendAsync(client, member, HttpMethod.Post, "/payment-methods/",
            new { providerPaymentMethodId = token, email = member.Email, billingAddress });
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        return (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, Member member)
    {
        var response = await SendAsync(client, member, HttpMethod.Get, "/payment-methods/");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Member member, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(InternalClaimsAuth.HeaderName, member.Claims);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }
}
