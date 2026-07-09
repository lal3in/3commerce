using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api.Endpoints;

/// <summary>
/// Admin management of tenant/storefront payment accounts (aui_10): list, create (Draft), check
/// readiness, and drive the lifecycle (submit → activate / suspend / archive). Activation enforces the
/// domain readiness rules (e.g. a Live account needs an external account ref).
/// </summary>
public static class PaymentAccountAdminEndpoints
{
    public static IEndpointRouteBuilder MapPaymentAccounts(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/payment-accounts").WithTags("Admin Payment Accounts")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapPost("/{id:guid}/make-default", MakeDefault);
        group.MapGet("/{id:guid}/readiness", Readiness);
        group.MapPost("/{id:guid}/submit", (Guid id, PaymentsDbContext db, CancellationToken ct) =>
            Transition(id, db, a => a.SubmitForApproval(DateTimeOffset.UtcNow), ct));
        group.MapPost("/{id:guid}/activate", (Guid id, PaymentsDbContext db, CancellationToken ct) =>
            Transition(id, db, a => a.Activate(DateTimeOffset.UtcNow), ct));
        group.MapPost("/{id:guid}/suspend", (Guid id, PaymentsDbContext db, CancellationToken ct) =>
            Transition(id, db, a => a.Suspend(DateTimeOffset.UtcNow), ct));
        group.MapPost("/{id:guid}/archive", (Guid id, PaymentsDbContext db, CancellationToken ct) =>
            Transition(id, db, a => a.Archive(DateTimeOffset.UtcNow), ct));
        return app;
    }

    private static async Task<Ok<List<PaymentAccountDto>>> List(Guid tenantId, PaymentsDbContext db, CancellationToken ct)
    {
        var accounts = await db.PaymentAccounts.AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.Name)
            .Select(a => ToDto(a))
            .ToListAsync(ct);
        return TypedResults.Ok(accounts);
    }

    private static async Task<Created<PaymentAccountDto>> Create(
        CreatePaymentAccountRequest request, PaymentsDbContext db, CancellationToken ct)
    {
        var account = PaymentAccount.Create(
            request.TenantId, request.StorefrontId, request.Name, request.Provider, request.Mode,
            request.IsDefaultForTenant, request.ExternalAccountRef, DateTimeOffset.UtcNow);
        db.PaymentAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/admin/payment-accounts/{account.Id}", ToDto(account));
    }

    private static async Task<Results<Ok<PaymentAccountDto>, NotFound, Conflict<string>>> Update(
        Guid id, UpdatePaymentAccountRequest request, PaymentsDbContext db, CancellationToken ct)
    {
        var account = await db.PaymentAccounts.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            account.UpdateDetails(request.Name, request.Provider, request.Mode, request.ExternalAccountRef, DateTimeOffset.UtcNow);
        }
        catch (PaymentAccountRuleException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(account));
    }

    // Makes one account the tenant default and unsets every sibling in a single tenant-scoped
    // transaction (one SaveChanges = one DB transaction), so a tenant never has two defaults.
    private static async Task<Results<Ok<PaymentAccountDto>, NotFound, Conflict<string>>> MakeDefault(
        Guid id, PaymentsDbContext db, CancellationToken ct)
    {
        var target = await db.PaymentAccounts.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (target is null)
        {
            return TypedResults.NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        try
        {
            target.SetAsDefault(now);
        }
        catch (PaymentAccountRuleException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }

        var siblings = await db.PaymentAccounts
            .Where(a => a.TenantId == target.TenantId && a.Id != target.Id && a.IsDefaultForTenant)
            .ToListAsync(ct);
        foreach (var sibling in siblings)
        {
            sibling.ClearDefault(now);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(target));
    }

    private static async Task<Results<Ok<PaymentAccountReadiness>, NotFound>> Readiness(
        Guid id, PaymentsDbContext db, CancellationToken ct)
    {
        var account = await db.PaymentAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        return account is null ? TypedResults.NotFound() : TypedResults.Ok(account.CheckReadiness());
    }

    private static async Task<Results<Ok<PaymentAccountDto>, NotFound, Conflict<string>>> Transition(
        Guid id, PaymentsDbContext db, Action<PaymentAccount> action, CancellationToken ct)
    {
        var account = await db.PaymentAccounts.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            action(account);
        }
        catch (PaymentAccountRuleException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(account));
    }

    private static PaymentAccountDto ToDto(PaymentAccount a) => new(
        a.Id, a.TenantId, a.StorefrontId, a.Name, a.Provider, a.Mode.ToString(), a.State.ToString(),
        a.IsDefaultForTenant, a.ExternalAccountRef, a.CreatedAt);
}

public record CreatePaymentAccountRequest(
    [property: Required] Guid TenantId,
    Guid? StorefrontId,
    [property: Required] string Name,
    [property: Required] string Provider,
    PaymentProviderMode Mode,
    bool IsDefaultForTenant,
    string? ExternalAccountRef);

public record UpdatePaymentAccountRequest(
    [property: Required] string Name,
    [property: Required] string Provider,
    PaymentProviderMode Mode,
    string? ExternalAccountRef);

public record PaymentAccountDto(
    Guid Id, Guid TenantId, Guid? StorefrontId, string Name, string Provider, string Mode, string State,
    bool IsDefaultForTenant, string? ExternalAccountRef, DateTimeOffset CreatedAt);
