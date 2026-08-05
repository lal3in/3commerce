using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
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
        group.MapPost("/{id:guid}/submit", (Guid id, PaymentsDbContext db, IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct) =>
            Transition(id, "submit", db, publisher, audit, user, a => a.SubmitForApproval(DateTimeOffset.UtcNow), ct));
        group.MapPost("/{id:guid}/activate", (Guid id, PaymentsDbContext db, IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct) =>
            Transition(id, "activate", db, publisher, audit, user, a => a.Activate(DateTimeOffset.UtcNow), ct));
        group.MapPost("/{id:guid}/suspend", (Guid id, PaymentsDbContext db, IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct) =>
            Transition(id, "suspend", db, publisher, audit, user, a => a.Suspend(DateTimeOffset.UtcNow), ct));
        group.MapPost("/{id:guid}/archive", (Guid id, PaymentsDbContext db, IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct) =>
            Transition(id, "archive", db, publisher, audit, user, a => a.Archive(DateTimeOffset.UtcNow), ct));
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

    private static async Task<Results<Created<PaymentAccountDto>, Conflict<string>>> Create(
        CreatePaymentAccountRequest request, PaymentsDbContext db, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct)
    {
        PaymentAccount account;
        try
        {
            account = PaymentAccount.Create(
                request.TenantId, request.StorefrontId, request.Name, request.Provider, request.Mode,
                request.IsDefaultForStorefront, request.ExternalAccountRef, DateTimeOffset.UtcNow);
        }
        catch (PaymentAccountRuleException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }

        db.PaymentAccounts.Add(account);
        await audit.RecordAsync(user.Mutation(
            account.TenantId, "PaymentAccount", account.Id.ToString(), "payments.payment_account.create", account.Name), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/admin/payment-accounts/{account.Id}", ToDto(account));
    }

    private static async Task<Results<Ok<PaymentAccountDto>, NotFound, Conflict<string>>> Update(
        Guid id, UpdatePaymentAccountRequest request, PaymentsDbContext db, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct)
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

        await audit.RecordAsync(user.Mutation(
            account.TenantId, "PaymentAccount", account.Id.ToString(), "payments.payment_account.update", account.Name), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(account));
    }

    // Makes one account the tenant default and unsets every sibling in a single tenant-scoped
    // transaction (one SaveChanges = one DB transaction), so a tenant never has two defaults.
    private static async Task<Results<Ok<PaymentAccountDto>, NotFound, Conflict<string>>> MakeDefault(
        Guid id, PaymentsDbContext db, IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct)
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
            .Where(a => a.TenantId == target.TenantId && a.StorefrontId == target.StorefrontId && a.Id != target.Id && a.IsDefaultForStorefront)
            .ToListAsync(ct);
        foreach (var sibling in siblings)
        {
            sibling.ClearDefault(now);
        }

        await audit.RecordAsync(user.Mutation(
            target.TenantId, "PaymentAccount", target.Id.ToString(), "payments.payment_account.make_default", target.Name), ct);
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
        Guid id, string transition, PaymentsDbContext db, IPublishEndpoint publisher, IAuditRecorder audit, ClaimsPrincipal user,
        Action<PaymentAccount> action, CancellationToken ct)
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

        await audit.RecordAsync(user.Mutation(
            account.TenantId, "PaymentAccount", account.Id.ToString(), $"payments.payment_account.{transition}", account.Name), ct);
        await db.SaveChangesAsync(ct);
        await PublishReadinessAsync(db, publisher, account.TenantId, account.StorefrontId, ct);
        return TypedResults.Ok(ToDto(account));
    }

    // Storefront payment-account readiness — an idempotent boolean Catalog's go-live gate reads (ADR-0042).
    // Published after save (post-change truth); a lost publish self-heals on the next account change.
    private static async Task PublishReadinessAsync(
        PaymentsDbContext db, IPublishEndpoint publisher, Guid tenantId, Guid storefrontId, CancellationToken ct)
    {
        var hasActive = await db.PaymentAccounts.AsNoTracking()
            .AnyAsync(a => a.TenantId == tenantId && a.StorefrontId == storefrontId && a.State == PaymentAccountState.Active, ct);
        await publisher.Publish(new StorefrontPaymentReadinessChanged(tenantId, storefrontId, hasActive), ct);
    }

    private static PaymentAccountDto ToDto(PaymentAccount a) => new(
        a.Id, a.TenantId, a.StorefrontId, a.Name, a.Provider, a.Mode.ToString(), a.State.ToString(),
        a.IsDefaultForStorefront, a.ExternalAccountRef, a.CreatedAt);
}

public record CreatePaymentAccountRequest(
    [property: Required] Guid TenantId,
    [property: Required] Guid StorefrontId,
    [property: Required] string Name,
    [property: Required] string Provider,
    PaymentProviderMode Mode,
    bool IsDefaultForStorefront,
    string? ExternalAccountRef);

public record UpdatePaymentAccountRequest(
    [property: Required] string Name,
    [property: Required] string Provider,
    PaymentProviderMode Mode,
    string? ExternalAccountRef);

public record PaymentAccountDto(
    Guid Id, Guid TenantId, Guid StorefrontId, string Name, string Provider, string Mode, string State,
    bool IsDefaultForStorefront, string? ExternalAccountRef, DateTimeOffset CreatedAt);
