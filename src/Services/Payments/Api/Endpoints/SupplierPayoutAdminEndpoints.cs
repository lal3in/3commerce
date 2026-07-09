using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api.Endpoints;

/// <summary>
/// Admin management of supplier payout setup (aui_10): tokenized/masked supplier bank accounts plus
/// payout instructions. Raw bank details never enter this service; callers provide a vault token ref
/// and masked display values only.
/// </summary>
public static class SupplierPayoutAdminEndpoints
{
    public static IEndpointRouteBuilder MapSupplierPayouts(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/supplier-payouts").WithTags("Admin Supplier Payouts")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/bank-accounts", ListBankAccounts);
        group.MapPost("/bank-accounts", CreateBankAccount);
        group.MapPut("/bank-accounts/{id:guid}", UpdateBankAccount);
        group.MapPost("/bank-accounts/{id:guid}/approve", ApproveBankAccount);
        group.MapPost("/bank-accounts/{id:guid}/reject", RejectBankAccount);
        group.MapPost("/bank-accounts/{id:guid}/archive", ArchiveBankAccount);

        group.MapGet("/instructions", ListInstructions);
        group.MapPost("/instructions", CreateInstruction);
        group.MapPut("/instructions/{id:guid}", UpdateInstruction);
        group.MapPost("/instructions/{id:guid}/deactivate", DeactivateInstruction);
        return app;
    }

    private static async Task<Ok<List<SupplierBankAccountDto>>> ListBankAccounts(
        Guid tenantId, Guid? supplierEntityId, PaymentsDbContext db, CancellationToken ct)
    {
        var query = db.SupplierBankAccounts.AsNoTracking().Where(a => a.TenantId == tenantId);
        if (supplierEntityId is not null)
        {
            query = query.Where(a => a.SupplierEntityId == supplierEntityId);
        }

        var accounts = await query
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => ToDto(a))
            .ToListAsync(ct);
        return TypedResults.Ok(accounts);
    }

    private static async Task<Created<SupplierBankAccountDto>> CreateBankAccount(
        CreateSupplierBankAccountRequest request, PaymentsDbContext db, CancellationToken ct)
    {
        var account = SupplierBankAccount.Create(
            request.TenantId, request.SupplierEntityId, request.AccountName, request.BankCountry,
            request.RoutingNumberMasked, request.AccountNumberMasked, request.AccountTokenRef, DateTimeOffset.UtcNow);

        db.SupplierBankAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/admin/supplier-payouts/bank-accounts/{account.Id}", ToDto(account));
    }

    // Edits label/masked fields only — raw bank details never enter this service. Changing the banking
    // identity (country/masked routing/masked account/token) resets an approved account to
    // PendingApproval (domain rule), so a re-keyed account is always re-verified before it can pay out.
    private static async Task<Results<Ok<SupplierBankAccountDto>, NotFound, Conflict<string>>> UpdateBankAccount(
        Guid id, UpdateSupplierBankAccountRequest request, PaymentsDbContext db, CancellationToken ct)
    {
        var account = await db.SupplierBankAccounts.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            account.UpdateDetails(
                request.AccountName, request.BankCountry, request.RoutingNumberMasked,
                request.AccountNumberMasked, request.AccountTokenRef);
        }
        catch (SupplierPayableRuleException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(account));
    }

    private static Task<Results<Ok<SupplierBankAccountDto>, NotFound, Conflict<string>>> ApproveBankAccount(
        Guid id, PayoutDecisionRequest request, PaymentsDbContext db, CancellationToken ct) =>
        TransitionBankAccount(id, db, a => a.Approve(request.Reason, DateTimeOffset.UtcNow), ct);

    private static Task<Results<Ok<SupplierBankAccountDto>, NotFound, Conflict<string>>> RejectBankAccount(
        Guid id, PayoutDecisionRequest request, PaymentsDbContext db, CancellationToken ct) =>
        TransitionBankAccount(id, db, a => a.Reject(request.Reason), ct);

    private static Task<Results<Ok<SupplierBankAccountDto>, NotFound, Conflict<string>>> ArchiveBankAccount(
        Guid id, PaymentsDbContext db, CancellationToken ct) =>
        TransitionBankAccount(id, db, a => a.Archive(), ct);

    private static async Task<Results<Ok<SupplierBankAccountDto>, NotFound, Conflict<string>>> TransitionBankAccount(
        Guid id, PaymentsDbContext db, Action<SupplierBankAccount> action, CancellationToken ct)
    {
        var account = await db.SupplierBankAccounts.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            action(account);
        }
        catch (SupplierPayableRuleException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(account));
    }

    private static async Task<Ok<List<PayoutInstructionDto>>> ListInstructions(
        Guid tenantId, Guid? supplierEntityId, PaymentsDbContext db, CancellationToken ct)
    {
        var query = db.PayoutInstructions.AsNoTracking().Where(i => i.TenantId == tenantId);
        if (supplierEntityId is not null)
        {
            query = query.Where(i => i.SupplierEntityId == supplierEntityId);
        }

        var instructions = await query
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => ToDto(i))
            .ToListAsync(ct);
        return TypedResults.Ok(instructions);
    }

    private static async Task<Results<Created<PayoutInstructionDto>, NotFound, Conflict<string>>> CreateInstruction(
        CreatePayoutInstructionRequest request, PaymentsDbContext db, CancellationToken ct)
    {
        var bankAccount = await db.SupplierBankAccounts.SingleOrDefaultAsync(a =>
            a.Id == request.BankAccountId && a.TenantId == request.TenantId && a.SupplierEntityId == request.SupplierEntityId, ct);
        if (bankAccount is null)
        {
            return TypedResults.NotFound();
        }

        if (!Enum.TryParse<PayoutCadence>(request.Cadence, ignoreCase: true, out var cadence))
        {
            return TypedResults.Conflict("Unsupported payout cadence.");
        }

        try
        {
            var existing = await db.PayoutInstructions
                .Where(i => i.TenantId == request.TenantId && i.SupplierEntityId == request.SupplierEntityId && i.Active)
                .ToListAsync(ct);
            foreach (var instruction in existing)
            {
                instruction.Deactivate();
            }

            var created = PayoutInstruction.Create(bankAccount, cadence, DateTimeOffset.UtcNow);
            db.PayoutInstructions.Add(created);
            await db.SaveChangesAsync(ct);
            return TypedResults.Created($"/admin/supplier-payouts/instructions/{created.Id}", ToDto(created));
        }
        catch (SupplierPayableRuleException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }
    }

    // Modify an existing instruction's schedule (cadence) and/or the bank account it routes to. The
    // target bank account must be Active (same guard as create); the enum arrives numeric on the wire.
    private static async Task<Results<Ok<PayoutInstructionDto>, NotFound, Conflict<string>>> UpdateInstruction(
        Guid id, UpdatePayoutInstructionRequest request, PaymentsDbContext db, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Cadence))
        {
            return TypedResults.Conflict("Unsupported payout cadence.");
        }

        var instruction = await db.PayoutInstructions.SingleOrDefaultAsync(i => i.Id == id, ct);
        if (instruction is null)
        {
            return TypedResults.NotFound();
        }

        var bankAccountId = request.BankAccountId ?? instruction.BankAccountId;
        var bankAccount = await db.SupplierBankAccounts.SingleOrDefaultAsync(a =>
            a.Id == bankAccountId && a.TenantId == instruction.TenantId && a.SupplierEntityId == instruction.SupplierEntityId, ct);
        if (bankAccount is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            instruction.Update(bankAccount, request.Cadence);
        }
        catch (SupplierPayableRuleException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(instruction));
    }

    private static async Task<Results<Ok<PayoutInstructionDto>, NotFound>> DeactivateInstruction(
        Guid id, PaymentsDbContext db, CancellationToken ct)
    {
        var instruction = await db.PayoutInstructions.SingleOrDefaultAsync(i => i.Id == id, ct);
        if (instruction is null)
        {
            return TypedResults.NotFound();
        }

        instruction.Deactivate();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(instruction));
    }

    private static SupplierBankAccountDto ToDto(SupplierBankAccount a) => new(
        a.Id, a.TenantId, a.SupplierEntityId, a.AccountName, a.BankCountry, a.RoutingNumberMasked,
        a.AccountNumberMasked, a.AccountTokenRef, a.State.ToString(), a.ApprovalReason, a.CreatedAt, a.ApprovedAt);

    private static PayoutInstructionDto ToDto(PayoutInstruction i) => new(
        i.Id, i.TenantId, i.SupplierEntityId, i.BankAccountId, i.Cadence.ToString(), i.Active, i.CreatedAt);
}

public record CreateSupplierBankAccountRequest(
    [property: Required] Guid TenantId,
    [property: Required] Guid SupplierEntityId,
    [property: Required] string AccountName,
    [property: Required, StringLength(2, MinimumLength = 2)] string BankCountry,
    [property: Required] string RoutingNumberMasked,
    [property: Required] string AccountNumberMasked,
    [property: Required] string AccountTokenRef);

public record UpdateSupplierBankAccountRequest(
    [property: Required] string AccountName,
    [property: Required, StringLength(2, MinimumLength = 2)] string BankCountry,
    [property: Required] string RoutingNumberMasked,
    [property: Required] string AccountNumberMasked,
    [property: Required] string AccountTokenRef);

public record PayoutDecisionRequest([property: Required] string Reason);

public record CreatePayoutInstructionRequest(
    [property: Required] Guid TenantId,
    [property: Required] Guid SupplierEntityId,
    [property: Required] Guid BankAccountId,
    [property: Required] string Cadence);

public record UpdatePayoutInstructionRequest(
    PayoutCadence Cadence,
    Guid? BankAccountId);

public record SupplierBankAccountDto(
    Guid Id, Guid TenantId, Guid SupplierEntityId, string AccountName, string BankCountry,
    string RoutingNumberMasked, string AccountNumberMasked, string AccountTokenRef, string State,
    string? ApprovalReason, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt);

public record PayoutInstructionDto(
    Guid Id, Guid TenantId, Guid SupplierEntityId, Guid BankAccountId, string Cadence, bool Active, DateTimeOffset CreatedAt);
