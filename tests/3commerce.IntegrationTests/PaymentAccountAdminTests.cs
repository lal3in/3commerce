using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Admin payment-account endpoints (aui_10): create (Draft) → submit → activate lifecycle, and the
/// readiness guard that a Live account cannot activate without an external account reference.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class PaymentAccountAdminTests(Phase4Fixture fixture)
{
    private sealed record AccountDto(Guid Id, Guid TenantId, Guid? StorefrontId, string Name, string Provider,
        string Mode, string State, bool IsDefaultForTenant, string? ExternalAccountRef, DateTimeOffset CreatedAt);

    private HttpClient Admin()
    {
        var client = fixture.Payments.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));
        return client;
    }

    [Fact]
    public async Task Test_account_goes_draft_to_active_through_submit_and_activate()
    {
        var tenant = Guid.NewGuid();
        using var admin = Admin();

        var created = await (await admin.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = tenant, name = "Main", provider = "stripe", mode = 1, isDefaultForTenant = true }))
            .Content.ReadFromJsonAsync<AccountDto>();
        Assert.Equal("Draft", created!.State);

        (await admin.PostAsync($"/admin/payment-accounts/{created.Id}/submit", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/admin/payment-accounts/{created.Id}/activate", null)).EnsureSuccessStatusCode();

        var list = await admin.GetFromJsonAsync<List<AccountDto>>($"/admin/payment-accounts?tenantId={tenant}");
        Assert.Equal("Active", Assert.Single(list!, a => a.Id == created.Id).State);
    }

    [Fact]
    public async Task Live_account_cannot_activate_without_an_external_reference()
    {
        var tenant = Guid.NewGuid();
        using var admin = Admin();

        var created = await (await admin.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = tenant, name = "Live", provider = "stripe", mode = 2, isDefaultForTenant = false }))
            .Content.ReadFromJsonAsync<AccountDto>();
        (await admin.PostAsync($"/admin/payment-accounts/{created!.Id}/submit", null)).EnsureSuccessStatusCode();

        var activate = await admin.PostAsync($"/admin/payment-accounts/{created.Id}/activate", null);
        Assert.Equal(HttpStatusCode.Conflict, activate.StatusCode);
    }
}
