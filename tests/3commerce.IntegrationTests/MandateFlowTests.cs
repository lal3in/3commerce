using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Phase 2 direct-debit mandates over HTTP: a verified customer sets up a mandate whose rail is derived from
/// the storefront currency, confirms it (mock provider confirms deterministically), and it becomes active.
/// A currency without a bank-debit rail is rejected.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class MandateFlowTests(Phase4Fixture fixture)
{
    private sealed record MandateSetupDto(Guid MandateId, string SetupIntentId, string? ClientSecret, string Scheme);
    private sealed record MandateDto(Guid Id, string Scheme, string Currency, string Status, DateTimeOffset CreatedAt);

    private HttpClient Client(string email)
    {
        var client = fixture.Payments.CreateClient();
        client.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName,
            fixture.MintInternalClaims(Guid.NewGuid(), "customer", email, Guid.NewGuid().ToString()));
        return client;
    }

    [Fact]
    public async Task A_eur_mandate_sets_up_on_the_sepa_rail_and_confirms_to_active()
    {
        using var client = Client("mandate-eur@example.com");

        var created = await client.PostAsJsonAsync("/mandates", new { email = "mandate-eur@example.com", currency = "EUR" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var setup = (await created.Content.ReadFromJsonAsync<MandateSetupDto>())!;
        Assert.Equal("Sepa", setup.Scheme);
        Assert.NotEqual(Guid.Empty, setup.MandateId);

        var confirmed = await client.PostAsync($"/mandates/{setup.MandateId}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var dto = (await confirmed.Content.ReadFromJsonAsync<MandateDto>())!;
        Assert.Equal("Active", dto.Status);

        var list = await client.GetFromJsonAsync<List<MandateDto>>("/mandates");
        Assert.Contains(list!, m => m.Id == setup.MandateId && m.Status == "Active" && m.Currency == "EUR");
    }

    [Fact]
    public async Task A_currency_without_a_bank_debit_rail_is_rejected()
    {
        using var client = Client("mandate-jpy@example.com");

        var response = await client.PostAsJsonAsync("/mandates", new { email = "mandate-jpy@example.com", currency = "JPY" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
