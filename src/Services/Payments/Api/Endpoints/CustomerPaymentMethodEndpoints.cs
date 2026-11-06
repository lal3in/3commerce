using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api.Endpoints;

public static class CustomerPaymentMethodEndpoints
{
    public static IEndpointRouteBuilder MapCustomerPaymentMethods(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/payment-methods")
            .WithTags("Payment methods")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy);
        group.MapGet("/", List);
        group.MapPost("/setup-intent", CreateSetupIntent);
        group.MapPost("/", Save);
        group.MapDelete("/{id:guid}", Remove);
        group.MapPost("/{id:guid}/default", MakeDefault);
        group.MapPut("/{id:guid}/billing-address", SetBillingAddress)
            .WithSummary("Save this card's own billing address (mem_2); checkout fills the order's bill-to from it. Never changes an amount.");
        group.MapDelete("/{id:guid}/billing-address", ClearBillingAddress)
            .WithSummary("Remove this card's billing address (mem_2); orders already placed keep their own bill-to.");
        return app;
    }

    private static Guid UserId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue("sub")!);

    private static Guid TenantId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue("tenant")!);

    private static async Task<Ok<List<SavedPaymentMethodDto>>> List(
        ClaimsPrincipal principal, PaymentsDbContext db, CancellationToken ct)
    {
        var userId = UserId(principal);
        var methods = await db.SavedPaymentMethods.AsNoTracking()
            .Where(m => m.UserId == userId && m.State == SavedPaymentMethodState.Active)
            .OrderByDescending(m => m.IsDefault)
            .ThenByDescending(m => m.CreatedAt)
            .ToListAsync(ct);
        return TypedResults.Ok(methods.Select(ToDto).ToList());
    }

    /// <summary>
    /// Saves the caller's card's own billing address (mem_2). 404 for a card that is not the caller's or not active (no
    /// existence leak); 400 problem+json naming the field (<c>name</c> / <c>line1</c> / <c>city</c> / <c>region</c> /
    /// <c>postcode</c> / <c>country</c>) for a malformed address, nothing saved.
    /// </summary>
    private static async Task<Results<Ok<SavedPaymentMethodDto>, ValidationProblem, NotFound>> SetBillingAddress(
        Guid id, CardBillingAddressRequest request, ClaimsPrincipal principal, PaymentsDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (TryCreateAddress(request, fieldPrefix: string.Empty, out var address) is { } refused)
        {
            return refused;
        }

        var userId = UserId(principal);
        var method = await db.SavedPaymentMethods.SingleOrDefaultAsync(m => m.Id == id && m.UserId == userId && m.State == SavedPaymentMethodState.Active, ct);
        if (method is null)
        {
            return TypedResults.NotFound();
        }

        method.SetBillingAddress(address, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(method));
    }

    /// <summary>Removes the caller's card's billing address (mem_2); 404 for a card that is not the caller's.</summary>
    private static async Task<Results<NoContent, NotFound>> ClearBillingAddress(
        Guid id, ClaimsPrincipal principal, PaymentsDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = UserId(principal);
        var method = await db.SavedPaymentMethods.SingleOrDefaultAsync(m => m.Id == id && m.UserId == userId && m.State == SavedPaymentMethodState.Active, ct);
        if (method is null)
        {
            return TypedResults.NotFound();
        }

        method.SetBillingAddress(null, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // Validates a requested billing address; on refusal returns the 400 problem+json whose `field` (and `errors` key)
    // names the part at fault in the request's camelCase, under `fieldPrefix` (e.g. "billingAddress.").
    private static ValidationProblem? TryCreateAddress(CardBillingAddressRequest request, string fieldPrefix, out CardBillingAddress? address)
    {
        try
        {
            address = CardBillingAddress.Create(request.Name, request.Line1, request.City, request.Region, request.Postcode, request.Country);
            return null;
        }
        catch (CardBillingAddressException ex)
        {
            address = null;
            var field = fieldPrefix + char.ToLowerInvariant(ex.Field[0]) + ex.Field[1..];
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]> { [field] = [ex.Message] },
                detail: ex.Message,
                title: "The billing address is invalid.",
                extensions: new Dictionary<string, object?> { ["field"] = field });
        }
    }

    private static async Task<Ok<SetupIntentDto>> CreateSetupIntent(
        SetupIntentRequest request,
        ClaimsPrincipal principal,
        PaymentsDbContext db,
        IPaymentProviderRegistry registry,
        TimeProvider time,
        CancellationToken ct)
    {
        var provider = registry.ResolveDefault();
        var customer = await GetOrCreateCustomerAsync(principal, request.Email, db, provider, time, ct);
        var intent = await provider.CreateSetupIntentAsync(customer.ProviderCustomerId, ct);
        return TypedResults.Ok(new SetupIntentDto(intent.SetupIntentId, intent.ClientSecret));
    }

    private static async Task<Results<Created<SavedPaymentMethodDto>, ValidationProblem>> Save(
        SavePaymentMethodRequest request,
        ClaimsPrincipal principal,
        PaymentsDbContext db,
        IPaymentProviderRegistry registry,
        TimeProvider time,
        CancellationToken ct)
    {
        // mem_2: an optional billing address for this card, checked before anything reaches the provider.
        CardBillingAddress? billingAddress = null;
        if (request.BillingAddress is { } requested
            && TryCreateAddress(requested, fieldPrefix: "billingAddress.", out billingAddress) is { } refused)
        {
            return refused;
        }

        var provider = registry.ResolveDefault();
        var customer = await GetOrCreateCustomerAsync(principal, request.Email, db, provider, time, ct);
        var details = await provider.GetPaymentMethodAsync(request.ProviderPaymentMethodId, ct);
        var now = time.GetUtcNow();
        var method = await db.SavedPaymentMethods.SingleOrDefaultAsync(
            m => m.PaymentCustomerId == customer.Id && m.ProviderPaymentMethodId == details.ProviderPaymentMethodId,
            ct);
        if (method is null)
        {
            method = new SavedPaymentMethod
            {
                Id = Guid.CreateVersion7(),
                PaymentCustomerId = customer.Id,
                TenantId = customer.TenantId,
                UserId = customer.UserId,
                Provider = customer.Provider,
                ProviderPaymentMethodId = details.ProviderPaymentMethodId,
                Brand = details.Brand,
                Last4 = details.Last4,
                ExpMonth = details.ExpMonth,
                ExpYear = details.ExpYear,
                CreatedAt = now,
            };
            db.SavedPaymentMethods.Add(method);
        }
        else
        {
            method.Brand = details.Brand;
            method.Last4 = details.Last4;
            method.ExpMonth = details.ExpMonth;
            method.ExpYear = details.ExpYear;
        }

        if (billingAddress is not null)
        {
            method.SetBillingAddress(billingAddress, now);
        }

        if (request.MakeDefault || !await db.SavedPaymentMethods.AnyAsync(m => m.UserId == customer.UserId && m.State == SavedPaymentMethodState.Active && m.Id != method.Id, ct))
        {
            await ClearDefaultsAsync(db, customer.UserId, now, ct);
            method.MakeDefault(now);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/payment-methods/{method.Id}", ToDto(method));
    }

    private static async Task<Results<NoContent, NotFound>> Remove(
        Guid id, ClaimsPrincipal principal, PaymentsDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = UserId(principal);
        var method = await db.SavedPaymentMethods.SingleOrDefaultAsync(m => m.Id == id && m.UserId == userId, ct);
        if (method is null)
        {
            return TypedResults.NotFound();
        }

        method.Remove(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> MakeDefault(
        Guid id, ClaimsPrincipal principal, PaymentsDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = UserId(principal);
        var method = await db.SavedPaymentMethods.SingleOrDefaultAsync(m => m.Id == id && m.UserId == userId && m.State == SavedPaymentMethodState.Active, ct);
        if (method is null)
        {
            return TypedResults.NotFound();
        }

        var now = time.GetUtcNow();
        await ClearDefaultsAsync(db, userId, now, ct);
        method.MakeDefault(now);
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
        var customer = await db.PaymentCustomers.SingleOrDefaultAsync(c => c.UserId == userId && c.Provider == "stripe", ct);
        if (customer is not null)
        {
            return customer;
        }

        customer = new PaymentCustomer
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            Provider = "stripe",
            ProviderCustomerId = await provider.CreateCustomerAsync(userId, email, ct),
            CreatedAt = time.GetUtcNow(),
        };
        db.PaymentCustomers.Add(customer);
        await db.SaveChangesAsync(ct);
        return customer;
    }

    private static async Task ClearDefaultsAsync(PaymentsDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var defaults = await db.SavedPaymentMethods.Where(m => m.UserId == userId && m.IsDefault).ToListAsync(ct);
        foreach (var method in defaults)
        {
            method.ClearDefault(now);
        }
    }

    private static SavedPaymentMethodDto ToDto(SavedPaymentMethod method) =>
        new(method.Id, method.Brand, method.Last4, method.ExpMonth, method.ExpYear, method.IsDefault,
            method.BillingAddress is { } a ? new CardBillingAddressDto(a.Name, a.Line1, a.City, a.Region, a.Postcode, a.Country) : null);
}

public record SetupIntentRequest([property: Required, EmailAddress] string Email);
public record SetupIntentDto(string SetupIntentId, string ClientSecret);

// mem_2 (appended): an optional billing address for the card being saved.
public record SavePaymentMethodRequest(
    [property: Required] string ProviderPaymentMethodId,
    [property: Required, EmailAddress] string Email,
    bool MakeDefault = false,
    CardBillingAddressRequest? BillingAddress = null);

/// <summary>A card's billing address (mem_2) — the bill-to shape. Validated by <see cref="CardBillingAddress.Create"/> so a refusal names the field.</summary>
public record CardBillingAddressRequest(string? Name, string? Line1, string? City, string? Postcode, string? Country, string? Region = null);

public record CardBillingAddressDto(string Name, string Line1, string City, string? Region, string Postcode, string Country);

// mem_2 (appended): the card's own billing address, null when it has none.
public record SavedPaymentMethodDto(Guid Id, string Brand, string Last4, int ExpMonth, int ExpYear, bool IsDefault, CardBillingAddressDto? BillingAddress = null);
