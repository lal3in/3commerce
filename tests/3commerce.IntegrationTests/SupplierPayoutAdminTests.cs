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
    public async Task Bank_account_label_edit_keeps_approval_but_identity_edit_resets_it()
    {
        var tenant = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        using var admin = Admin();

        var created = await CreateBankAccountAsync(admin, tenant, supplier, "vault://bank/edit-me");
        (await admin.PostAsJsonAsync($"/admin/supplier-payouts/bank-accounts/{created.Id}/approve",
            new { reason = "verified by operator" })).EnsureSuccessStatusCode();

        // Label-only change: state stays Active.
        var relabeled = await (await admin.PutAsJsonAsync($"/admin/supplier-payouts/bank-accounts/{created.Id}", new
        {
            accountName = "Renamed settlement account",
            bankCountry = created.BankCountry,
            routingNumberMasked = created.RoutingNumberMasked,
            accountNumberMasked = created.AccountNumberMasked,
            accountTokenRef = created.AccountTokenRef
        })).Content.ReadFromJsonAsync<BankAccountDto>();
        Assert.Equal("Renamed settlement account", relabeled!.AccountName);
        Assert.Equal("Active", relabeled.State);

        // Banking-identity change (new vault token): approval is reset to pending.
        var rekeyed = await (await admin.PutAsJsonAsync($"/admin/supplier-payouts/bank-accounts/{created.Id}", new
        {
            accountName = relabeled.AccountName,
            bankCountry = relabeled.BankCountry,
            routingNumberMasked = relabeled.RoutingNumberMasked,
            accountNumberMasked = "******5555",
            accountTokenRef = "vault://bank/rotated"
        })).Content.ReadFromJsonAsync<BankAccountDto>();
        Assert.Equal("PendingApproval", rekeyed!.State);
        Assert.Null(rekeyed.ApprovalReason);
        Assert.Null(rekeyed.ApprovedAt);
    }

    [Fact]
    public async Task Instruction_edit_changes_cadence_and_can_repoint_to_another_approved_account()
    {
        var tenant = Guid.NewGuid();
        var supplier = Guid.NewGuid();
        using var admin = Admin();

        var first = await CreateBankAccountAsync(admin, tenant, supplier, "vault://bank/first");
        (await admin.PostAsJsonAsync($"/admin/supplier-payouts/bank-accounts/{first.Id}/approve",
            new { reason = "verified by operator" })).EnsureSuccessStatusCode();

        var instruction = await (await admin.PostAsJsonAsync("/admin/supplier-payouts/instructions", new
        {
            tenantId = tenant,
            supplierEntityId = supplier,
            bankAccountId = first.Id,
            cadence = "Weekly"
        })).Content.ReadFromJsonAsync<InstructionDto>();

        // Cadence-only edit (enum is numeric on the wire: Monthly = 4).
        var monthly = await (await admin.PutAsJsonAsync($"/admin/supplier-payouts/instructions/{instruction!.Id}",
            new { cadence = 4 })).Content.ReadFromJsonAsync<InstructionDto>();
        Assert.Equal("Monthly", monthly!.Cadence);
        Assert.Equal(first.Id, monthly.BankAccountId);

        // Re-point to a second approved account.
        var second = await CreateBankAccountAsync(admin, tenant, supplier, "vault://bank/second");
        (await admin.PostAsJsonAsync($"/admin/supplier-payouts/bank-accounts/{second.Id}/approve",
            new { reason = "verified by operator" })).EnsureSuccessStatusCode();

        var repointed = await (await admin.PutAsJsonAsync($"/admin/supplier-payouts/instructions/{instruction.Id}",
            new { cadence = 4, bankAccountId = second.Id })).Content.ReadFromJsonAsync<InstructionDto>();
        Assert.Equal(second.Id, repointed!.BankAccountId);

        // A pending (unapproved) account cannot receive the instruction.
        var pending = await CreateBankAccountAsync(admin, tenant, supplier, "vault://bank/pending-edit");
        var conflict = await admin.PutAsJsonAsync($"/admin/supplier-payouts/instructions/{instruction.Id}",
            new { cadence = 2, bankAccountId = pending.Id });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    private static async Task<BankAccountDto> CreateBankAccountAsync(HttpClient admin, Guid tenant, Guid supplier, string tokenRef)
    {
        var response = await admin.PostAsJsonAsync("/admin/supplier-payouts/bank-accounts", new
        {
            tenantId = tenant,
            supplierEntityId = supplier,
            accountName = "Supplier settlement",
            bankCountry = "AU",
            routingNumberMasked = "***-123",
            accountNumberMasked = "******9876",
            accountTokenRef = tokenRef
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BankAccountDto>())!;
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
