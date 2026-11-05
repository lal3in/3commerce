using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
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
        return app;
    }

    private static Guid UserId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue("sub")!);

    private static Guid TenantId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue("tenant")!);

    private static async Task<Ok<List<SavedPaymentMethodDto>>> List(
        ClaimsPrincipal principal, PaymentsDbContext db, IPublishEndpoint publisher, TimeProvider time, CancellationToken ct)
    {
        var userId = UserId(principal);

        // A vault created before the customer's email was recorded learns it here (the account page lists the cards), so a
        // usage auto top-up can find the customer's default card (jobmgr_3_settlement) — and top-ups paused for want of one
        // resume when that card already exists.
        var customer = await db.PaymentCustomers.SingleOrDefaultAsync(c => c.UserId == userId && c.Provider == "stripe", ct);
        if (customer is not null && CustomerClaims.TryRead(principal, out _, out var email) && customer.RecordEmail(email))
        {
            await PublishReadyIfDefaultAsync(customer, db, publisher, time.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
        }

        var methods = await db.SavedPaymentMethods.AsNoTracking()
            .Where(m => m.UserId == userId && m.State == SavedPaymentMethodState.Active)
            .OrderByDescending(m => m.IsDefault)
            .ThenByDescending(m => m.CreatedAt)
            .Select(m => new SavedPaymentMethodDto(m.Id, m.Brand, m.Last4, m.ExpMonth, m.ExpYear, m.IsDefault))
            .ToListAsync(ct);
        return TypedResults.Ok(methods);
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
        await db.SaveChangesAsync(ct); // the vault's email, when it was just learnt
        return TypedResults.Ok(new SetupIntentDto(intent.SetupIntentId, intent.ClientSecret));
    }

    private static async Task<Created<SavedPaymentMethodDto>> Save(
        SavePaymentMethodRequest request,
        ClaimsPrincipal principal,
        PaymentsDbContext db,
        IPaymentProviderRegistry registry,
        IPublishEndpoint publisher,
        TimeProvider time,
        CancellationToken ct)
    {
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

        if (request.MakeDefault || !await db.SavedPaymentMethods.AnyAsync(m => m.UserId == customer.UserId && m.State == SavedPaymentMethodState.Active && m.Id != method.Id, ct))
        {
            await ClearDefaultsAsync(db, customer.UserId, now, ct);
            method.MakeDefault(now);
        }

        if (method.IsDefault)
        {
            await PublishReadyAsync(customer, publisher, now, ct);
        }
        else
        {
            await PublishReadyIfDefaultAsync(customer, db, publisher, now, ct);
        }

        await db.SaveChangesAsync(ct); // the card and CustomerPaymentMethodReady commit together (bus outbox)
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
        Guid id, ClaimsPrincipal principal, PaymentsDbContext db, IPublishEndpoint publisher, TimeProvider time, CancellationToken ct)
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
        var customer = await db.PaymentCustomers.SingleAsync(c => c.Id == method.PaymentCustomerId, ct);
        if (CustomerClaims.TryRead(principal, out _, out var email))
        {
            customer.RecordEmail(email);
        }

        await PublishReadyAsync(customer, publisher, now, ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Tell Usage the customer has a default card to charge (<see cref="CustomerPaymentMethodReady"/>, bus outbox — the
    /// caller's SaveChanges commits it): auto-load top-ups paused for want of a card resume (owner decision 2026-11-06).
    /// Nothing is published while the vault's email is unknown — a usage charge could not find the card either.
    /// </summary>
    private static async Task PublishReadyAsync(PaymentCustomer customer, IPublishEndpoint publisher, DateTimeOffset now, CancellationToken ct)
    {
        if (customer.Email is { } email)
        {
            await publisher.Publish(new CustomerPaymentMethodReady(customer.TenantId, email, StoredTime.Of(now)), ct);
        }
    }

    private static async Task PublishReadyIfDefaultAsync(
        PaymentCustomer customer, PaymentsDbContext db, IPublishEndpoint publisher, DateTimeOffset now, CancellationToken ct)
    {
        if (await DefaultSavedCard.ExistsForCustomerAsync(db, customer.Id, ct))
        {
            await PublishReadyAsync(customer, publisher, now, ct);
        }
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
        // The signed-in customer's own email (gateway claims) names the vault; the request's copy is only a fallback.
        var customerEmail = CustomerClaims.TryRead(principal, out _, out var claimed) ? claimed : email;
        var customer = await db.PaymentCustomers.SingleOrDefaultAsync(c => c.UserId == userId && c.Provider == "stripe", ct);
        if (customer is not null)
        {
            customer.RecordEmail(customerEmail); // saved by the caller
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
        customer.RecordEmail(customerEmail);
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
        new(method.Id, method.Brand, method.Last4, method.ExpMonth, method.ExpYear, method.IsDefault);
}

public record SetupIntentRequest([property: Required, EmailAddress] string Email);
public record SetupIntentDto(string SetupIntentId, string ClientSecret);
public record SavePaymentMethodRequest([property: Required] string ProviderPaymentMethodId, [property: Required, EmailAddress] string Email, bool MakeDefault = false);
public record SavedPaymentMethodDto(Guid Id, string Brand, string Last4, int ExpMonth, int ExpYear, bool IsDefault);
