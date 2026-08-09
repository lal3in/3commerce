using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api.Endpoints;

/// <summary>
/// Direct-debit mandate setup for recurring/periodic payments in a non-card currency (Phase 2). The scheme
/// is derived from the storefront/settlement currency (USD→ACH, EUR→SEPA, GBP→Bacs, AUD→BECS, CAD→ACSS);
/// any other currency is card-only and rejected. Provider calls go through the resolved adapter's optional
/// <see cref="IDirectDebitProvider"/> capability — mode-gated for real providers, deterministic in mock.
/// </summary>
public static class MandateEndpoints
{
    public static IEndpointRouteBuilder MapMandates(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/mandates")
            .WithTags("Direct-debit mandates")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy);
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPost("/{id:guid}/confirm", Confirm);
        group.MapPost("/{id:guid}/revoke", Revoke);
        return app;
    }

    private static Guid UserId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue("sub")!);

    private static Guid TenantId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue("tenant")!);

    private static async Task<Ok<List<MandateDto>>> List(
        ClaimsPrincipal principal, PaymentsDbContext db, CancellationToken ct)
    {
        var userId = UserId(principal);
        var mandates = await db.Mandates.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new MandateDto(m.Id, m.Scheme.ToString(), m.Currency, m.Status.ToString(), m.CreatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(mandates);
    }

    private static async Task<Results<Created<MandateSetupDto>, BadRequest<string>>> Create(
        CreateMandateRequest request,
        ClaimsPrincipal principal,
        PaymentsDbContext db,
        IPaymentProviderRegistry registry,
        TimeProvider time,
        CancellationToken ct)
    {
        // The rail is fixed by the storefront currency — no rail, no direct debit (card-only currency).
        var scheme = DirectDebitSchemes.ForCurrency(request.Currency);
        if (scheme is null)
        {
            return TypedResults.BadRequest($"No direct-debit rail is available for currency '{request.Currency}'. Use a card instead.");
        }

        var provider = registry.ResolveDefault();
        if (provider is not IDirectDebitProvider dd)
        {
            return TypedResults.BadRequest($"Provider '{provider.ProviderKey}' does not support direct-debit mandates.");
        }

        var customer = await GetOrCreateCustomerAsync(principal, request.Email, db, provider, time, ct);
        var setup = await dd.CreateMandateSetupAsync(customer.ProviderCustomerId, scheme.Value, request.Currency, ct);

        var mandate = Mandate.Start(
            customer.TenantId, customer.UserId, customer.Id, provider.ProviderKey,
            scheme.Value, request.Currency, setup.SetupIntentId, time.GetUtcNow());
        db.Mandates.Add(mandate);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created(
            $"/mandates/{mandate.Id}",
            new MandateSetupDto(mandate.Id, setup.SetupIntentId, setup.ClientSecret, scheme.Value.ToString()));
    }

    private static async Task<Results<Ok<MandateDto>, NotFound, Conflict<string>>> Confirm(
        Guid id,
        ClaimsPrincipal principal,
        PaymentsDbContext db,
        IPaymentProviderRegistry registry,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = UserId(principal);
        var mandate = await db.Mandates.SingleOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct);
        if (mandate is null)
        {
            return TypedResults.NotFound();
        }

        if (mandate.Status == MandateStatus.Active)
        {
            return TypedResults.Ok(ToDto(mandate)); // idempotent
        }

        var provider = registry.ResolveDefault();
        if (provider is not IDirectDebitProvider dd)
        {
            return TypedResults.Conflict($"Provider '{provider.ProviderKey}' does not support direct-debit mandates.");
        }

        var confirmation = await dd.GetMandateAsync(mandate.ProviderSetupIntentId, ct);
        if (!confirmation.Confirmed || confirmation.ProviderMandateId is null || confirmation.ProviderPaymentMethodId is null)
        {
            // Bank debits settle asynchronously — the customer hasn't completed acceptance yet.
            return TypedResults.Conflict("Mandate is not confirmed yet.");
        }

        mandate.Activate(confirmation.ProviderMandateId, confirmation.ProviderPaymentMethodId, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(mandate));
    }

    private static async Task<Results<NoContent, NotFound>> Revoke(
        Guid id, ClaimsPrincipal principal, PaymentsDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = UserId(principal);
        var mandate = await db.Mandates.SingleOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct);
        if (mandate is null)
        {
            return TypedResults.NotFound();
        }

        mandate.Revoke(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<PaymentCustomer> GetOrCreateCustomerAsync(
        ClaimsPrincipal principal,
        string email,
        PaymentsDbContext db,
        IPaymentProvider provider,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = UserId(principal);
        var tenantId = TenantId(principal);
        var customer = await db.PaymentCustomers.SingleOrDefaultAsync(c => c.UserId == userId && c.Provider == provider.ProviderKey, ct);
        if (customer is not null)
        {
            return customer;
        }

        customer = new PaymentCustomer
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            Provider = provider.ProviderKey,
            ProviderCustomerId = await provider.CreateCustomerAsync(userId, email, ct),
            CreatedAt = time.GetUtcNow(),
        };
        db.PaymentCustomers.Add(customer);
        await db.SaveChangesAsync(ct);
        return customer;
    }

    private static MandateDto ToDto(Mandate m) =>
        new(m.Id, m.Scheme.ToString(), m.Currency, m.Status.ToString(), m.CreatedAt);
}

public record CreateMandateRequest([property: Required, EmailAddress] string Email, [property: Required] string Currency);
public record MandateSetupDto(Guid MandateId, string SetupIntentId, string? ClientSecret, string Scheme);
public record MandateDto(Guid Id, string Scheme, string Currency, string Status, DateTimeOffset CreatedAt);
