using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Webhook secret registry admin (def_2 / mt6_7): secrets are write-only — every response is
/// masked — and deactivation removes them from rotation while keeping the audit row.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class WebhookSecretAdminTests(Phase4Fixture fixture)
{
    private sealed record SecretDto(Guid Id, string Provider, string Masked, string? Label, bool Active,
        DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt);

    private HttpClient Admin()
    {
        var client = fixture.Payments.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));
        return client;
    }

    [Fact]
    public async Task Secrets_are_write_only_and_deactivation_ends_rotation()
    {
        using var admin = Admin();
        var secretValue = $"whsec_integration_{Guid.NewGuid():N}";

        // Create: the response is masked — the raw value never comes back.
        var create = await admin.PostAsJsonAsync("/admin/webhook-secrets",
            new { provider = "Stripe", secret = secretValue, label = "rotation test" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<SecretDto>())!;
        Assert.Equal("stripe", created.Provider); // normalized
        Assert.True(created.Active);
        Assert.DoesNotContain(secretValue, created.Masked);
        Assert.StartsWith("whse", created.Masked);
        Assert.EndsWith(secretValue[^4..], created.Masked);

        // Listing is masked too.
        var listed = (await admin.GetFromJsonAsync<List<SecretDto>>("/admin/webhook-secrets?provider=stripe"))!;
        var row = Assert.Single(listed, s => s.Id == created.Id);
        Assert.DoesNotContain(secretValue, row.Masked);

        // Deactivate: leaves the audit row, flags it out of rotation; a second deactivate 404s.
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsync($"/admin/webhook-secrets/{created.Id}/deactivate", null)).StatusCode);
        var after = (await admin.GetFromJsonAsync<List<SecretDto>>("/admin/webhook-secrets?provider=stripe"))!;
        var deactivated = Assert.Single(after, s => s.Id == created.Id);
        Assert.False(deactivated.Active);
        Assert.NotNull(deactivated.DeactivatedAt);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsync($"/admin/webhook-secrets/{created.Id}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task Registry_requires_the_admin_role()
    {
        using var client = fixture.Payments.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "customer"));

        var response = await client.GetAsync("/admin/webhook-secrets");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
