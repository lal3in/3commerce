using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Admin supplier-payout endpoints (aui_10): tokenized/masked bank-account approval and active payout
/// instruction creation. Raw bank details are deliberately absent from the API shape.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class SupplierPayoutAdminTests(Phase4Fixture fixture)
{
    private sealed record BankAccountDto(Guid Id, Guid TenantId, Guid SupplierEntityId, string AccountName,
        string BankCountry, string RoutingNumberMasked, string AccountNumberMasked, string AccountTokenRef,
        string State, string? ApprovalReason, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt);

    private sealed record InstructionDto(Guid Id, Guid TenantId, Guid SupplierEntityId, Guid BankAccountId,
        string Cadence, bool Active, DateTimeOffset CreatedAt);

    private HttpClient Admin()
    {
        var client = fixture.Payments.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));
        return client;
    }

    [Fact]
    public async Task Approved_bank_account_can_receive_an_active_payout_instruction()
    {
        var tenant = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        using var admin = Admin();

        var created = await (await admin.PostAsJsonAsync("/admin/supplier-payouts/bank-accounts", new
        {
            tenantId = tenant,
            supplierEntityId = supplier,
            accountName = "Supplier settlement",
            bankCountry = "AU",
            routingNumberMasked = "***-123",
            accountNumberMasked = "******9876",
            accountTokenRef = "vault://bank/abc"
        })).Content.ReadFromJsonAsync<BankAccountDto>();
        Assert.Equal("PendingApproval", created!.State);

        var approved = await (await admin.PostAsJsonAsync($"/admin/supplier-payouts/bank-accounts/{created.Id}/approve",
            new { reason = "verified by operator" })).Content.ReadFromJsonAsync<BankAccountDto>();
        Assert.Equal("Active", approved!.State);

        var instruction = await (await admin.PostAsJsonAsync("/admin/supplier-payouts/instructions", new
        {
            tenantId = tenant,
            supplierEntityId = supplier,
            bankAccountId = created.Id,
            cadence = "Fortnightly"
        })).Content.ReadFromJsonAsync<InstructionDto>();
        Assert.True(instruction!.Active);
        Assert.Equal("Fortnightly", instruction.Cadence);

        var instructions = await admin.GetFromJsonAsync<List<InstructionDto>>($"/admin/supplier-payouts/instructions?tenantId={tenant}&supplierEntityId={supplier}");
        Assert.Equal(instruction.Id, Assert.Single(instructions!).Id);
    }

    [Fact]
    public async Task Pending_bank_account_cannot_receive_a_payout_instruction()
    {
        var tenant = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        using var admin = Admin();

        var created = await (await admin.PostAsJsonAsync("/admin/supplier-payouts/bank-accounts", new
        {
            tenantId = tenant,
            supplierEntityId = supplier,
            accountName = "Pending supplier",
            bankCountry = "AU",
            routingNumberMasked = "***-456",
            accountNumberMasked = "******2222",
            accountTokenRef = "vault://bank/pending"
        })).Content.ReadFromJsonAsync<BankAccountDto>();

        var response = await admin.PostAsJsonAsync("/admin/supplier-payouts/instructions", new
        {
            tenantId = tenant,
            supplierEntityId = supplier,
            bankAccountId = created!.Id,
            cadence = "Weekly"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
