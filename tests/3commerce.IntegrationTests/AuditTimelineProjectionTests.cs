using System.Diagnostics;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// mr_8: admin mutations feed the Mission Control activity timeline. A producing service records an
/// audit entry in the same unit of work as the mutation (AuditEntryRecorded via the bus outbox); the
/// Audit service projects it and serves it from GET /admin/audit — the endpoint Mission Control reads.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class AuditTimelineProjectionTests(Phase4Fixture fixture)
{
    private sealed record AuditEntryDto(
        Guid Id, long Sequence, DateTimeOffset OccurredAt, Guid? ActorId, string? ActorRole,
        string Action, string ResourceType, string ResourceId, string Outcome, string? Summary);

    private HttpClient PaymentsAdmin(Guid actorId)
    {
        var client = fixture.Payments.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(actorId, "admin"));
        return client;
    }

    private HttpClient AuditAdmin()
    {
        var client = fixture.Audit.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));
        return client;
    }

    [Fact]
    public async Task Payment_account_create_and_lifecycle_land_in_the_audit_timeline()
    {
        var tenant = Guid.NewGuid();
        var actor = Guid.CreateVersion7();
        using var payments = PaymentsAdmin(actor);

        var create = await payments.PostAsJsonAsync("/admin/payment-accounts",
            new { tenantId = tenant, name = "Audited account", provider = "stripe", mode = 1, isDefaultForTenant = false });
        create.EnsureSuccessStatusCode();
        var account = await create.Content.ReadFromJsonAsync<AccountRef>();

        (await payments.PostAsync($"/admin/payment-accounts/{account!.Id}/submit", null)).EnsureSuccessStatusCode();

        var created = await WaitForEntryAsync(tenant, "payments.payment_account.create");
        Assert.Equal("PaymentAccount", created.ResourceType);
        Assert.Equal(account.Id.ToString(), created.ResourceId);
        Assert.Equal(actor, created.ActorId);
        Assert.Equal("admin", created.ActorRole);
        Assert.Equal("Success", created.Outcome);
        Assert.Equal("Audited account", created.Summary);

        var submitted = await WaitForEntryAsync(tenant, "payments.payment_account.submit");
        Assert.Equal(account.Id.ToString(), submitted.ResourceId);
    }

    [Fact]
    public async Task Xero_mapping_upsert_lands_in_the_audit_timeline()
    {
        var tenant = Guid.NewGuid();
        var actor = Guid.CreateVersion7();
        using var payments = PaymentsAdmin(actor);

        var create = await payments.PostAsJsonAsync("/admin/xero/mappings",
            new { tenantId = tenant, scope = 1, ledgerAccountCode = "4000", xeroAccountCode = "200" });
        create.EnsureSuccessStatusCode();

        var entry = await WaitForEntryAsync(tenant, "payments.xero_mapping.upsert");
        Assert.Equal("XeroAccountMapping", entry.ResourceType);
        Assert.Equal(actor, entry.ActorId);
        Assert.Equal("4000→200", entry.Summary);
    }

    private async Task<AuditEntryDto> WaitForEntryAsync(Guid tenantId, string action)
    {
        using var audit = AuditAdmin();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            var entries = await audit.GetFromJsonAsync<List<AuditEntryDto>>($"/admin/audit?tenantId={tenantId}&action={action}");
            if (entries is { Count: > 0 })
            {
                return entries[0];
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Audit entry '{action}' for tenant {tenantId} did not appear in the central projection.");
    }

    private sealed record AccountRef(Guid Id);
}
