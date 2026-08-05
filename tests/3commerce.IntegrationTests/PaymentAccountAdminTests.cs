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
    private sealed record AccountDto(Guid Id, Guid TenantId, Guid StorefrontId, string Name, string Provider,
        string Mode, string State, bool IsDefaultForStorefront, string? ExternalAccountRef, DateTimeOffset CreatedAt);

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
        var storefront = Guid.NewGuid();
        using var admin = Admin();

        var created = await (await admin.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = tenant, storefrontId = storefront, name = "Main", provider = "stripe", mode = 1, isDefaultForStorefront = true }))
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
        var storefront = Guid.NewGuid();
        using var admin = Admin();

        var created = await (await admin.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = tenant, storefrontId = storefront, name = "Live", provider = "stripe", mode = 2, isDefaultForStorefront = false }))
            .Content.ReadFromJsonAsync<AccountDto>();
        (await admin.PostAsync($"/admin/payment-accounts/{created!.Id}/submit", null)).EnsureSuccessStatusCode();

        var activate = await admin.PostAsync($"/admin/payment-accounts/{created.Id}/activate", null);
        Assert.Equal(HttpStatusCode.Conflict, activate.StatusCode);
    }

    [Fact]
    public async Task Edit_round_trips_mutable_fields()
    {
        var tenant = Guid.NewGuid();
        var storefront = Guid.NewGuid();
        using var admin = Admin();

        var created = await (await admin.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = tenant, storefrontId = storefront, name = "Old name", provider = "stripe", mode = 1, isDefaultForStorefront = false }))
            .Content.ReadFromJsonAsync<AccountDto>();

        var updated = await (await admin.PutAsJsonAsync($"/admin/payment-accounts/{created!.Id}",
            new { name = "New name", provider = "adyen", mode = 2, externalAccountRef = "acct_live_1" }))
            .Content.ReadFromJsonAsync<AccountDto>();

        Assert.Equal("New name", updated!.Name);
        Assert.Equal("adyen", updated.Provider);
        Assert.Equal("Live", updated.Mode);
        Assert.Equal("acct_live_1", updated.ExternalAccountRef);

        var listed = Assert.Single((await admin.GetFromJsonAsync<List<AccountDto>>($"/admin/payment-accounts?tenantId={tenant}"))!);
        Assert.Equal("New name", listed.Name);
        Assert.Equal("adyen", listed.Provider);
    }

    [Fact]
    public async Task Active_account_cannot_change_provider_or_mode_but_can_rename()
    {
        var tenant = Guid.NewGuid();
        var storefront = Guid.NewGuid();
        using var admin = Admin();

        var created = await (await admin.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = tenant, storefrontId = storefront, name = "Main", provider = "stripe", mode = 1, isDefaultForStorefront = false }))
            .Content.ReadFromJsonAsync<AccountDto>();
        (await admin.PostAsync($"/admin/payment-accounts/{created!.Id}/submit", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/admin/payment-accounts/{created.Id}/activate", null)).EnsureSuccessStatusCode();

        var providerChange = await admin.PutAsJsonAsync($"/admin/payment-accounts/{created.Id}",
            new { name = "Main", provider = "adyen", mode = 1, externalAccountRef = (string?)null });
        Assert.Equal(HttpStatusCode.Conflict, providerChange.StatusCode);

        var rename = await (await admin.PutAsJsonAsync($"/admin/payment-accounts/{created.Id}",
            new { name = "Renamed while active", provider = "stripe", mode = 1, externalAccountRef = (string?)null }))
            .Content.ReadFromJsonAsync<AccountDto>();
        Assert.Equal("Renamed while active", rename!.Name);
        Assert.Equal("Active", rename.State);
    }

    [Fact]
    public async Task Make_default_flips_exactly_one_account_on_and_the_rest_off()
    {
        var tenant = Guid.NewGuid();
        var storefront = Guid.NewGuid();
        using var admin = Admin();

        var first = await (await admin.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = tenant, storefrontId = storefront, name = "First", provider = "stripe", mode = 1, isDefaultForStorefront = true }))
            .Content.ReadFromJsonAsync<AccountDto>();
        var second = await (await admin.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = tenant, storefrontId = storefront, name = "Second", provider = "stripe", mode = 1, isDefaultForStorefront = false }))
            .Content.ReadFromJsonAsync<AccountDto>();

        var made = await (await admin.PostAsync($"/admin/payment-accounts/{second!.Id}/make-default", null))
            .Content.ReadFromJsonAsync<AccountDto>();
        Assert.True(made!.IsDefaultForStorefront);

        var list = await admin.GetFromJsonAsync<List<AccountDto>>($"/admin/payment-accounts?tenantId={tenant}");
        Assert.True(Assert.Single(list!, a => a.IsDefaultForStorefront).Id == second.Id);
        Assert.False(Assert.Single(list!, a => a.Id == first!.Id).IsDefaultForStorefront);
    }

    [Fact]
    public async Task Archived_account_cannot_become_default()
    {
        var tenant = Guid.NewGuid();
        var storefront = Guid.NewGuid();
        using var admin = Admin();

        var created = await (await admin.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = tenant, storefrontId = storefront, name = "Doomed", provider = "stripe", mode = 1, isDefaultForStorefront = false }))
            .Content.ReadFromJsonAsync<AccountDto>();
        (await admin.PostAsync($"/admin/payment-accounts/{created!.Id}/archive", null)).EnsureSuccessStatusCode();

        var response = await admin.PostAsync($"/admin/payment-accounts/{created.Id}/make-default", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Duplicating_a_storefront_clones_its_payment_accounts()
    {
        var tenant = new Guid("00000000-0000-0000-0000-000000000001");
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        using var admin = Admin();

        var created = await (await admin.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = tenant, storefrontId = source, name = "Source acct", provider = "stripe", mode = 1, isDefaultForStorefront = true }))
            .Content.ReadFromJsonAsync<AccountDto>();
        Assert.Equal(source, created!.StorefrontId);

        await fixture.PublishAsync(new ThreeCommerce.BuildingBlocks.Contracts.Catalog.StorefrontDuplicated(tenant, source, target, "Target"));

        AccountDto? clone = null;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var all = await admin.GetFromJsonAsync<List<AccountDto>>($"/admin/payment-accounts?tenantId={tenant}");
            clone = all!.FirstOrDefault(a => a.StorefrontId == target);
            if (clone is not null)
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.NotNull(clone);
        Assert.Equal("Source acct", clone!.Name);
        Assert.True(clone.IsDefaultForStorefront);
        Assert.Equal(target, clone.StorefrontId);
    }

    [Fact]
    public async Task Malformed_body_is_a_400_problem_not_a_500()
    {
        using var admin = Admin();

        // Enums bind as numbers on this platform (AGENTS.md); a string value must be a clear
        // client error naming the parameter — this class of mistake used to surface as a 500
        // (review finding F7 / rev_7).
        var response = await admin.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = Guid.NewGuid(), name = "Bad", provider = "stripe", mode = "Test", isDefaultForStorefront = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("request body", problem, StringComparison.OrdinalIgnoreCase);
    }
}
