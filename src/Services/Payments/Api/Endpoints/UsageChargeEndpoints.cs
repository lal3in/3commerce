using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Consumers;

namespace ThreeCommerce.Payments.Api.Endpoints;

/// <summary>
/// The storefront "complete payment" page of a usage charge (<c>usage_charge_pay_page</c>, mirroring the subscription pay
/// page of ADR-0057's 2026-10-27 amendment): the signed-in customer's unpaid overage / auto-load charges, what one owes, and
/// their own attempt to pay it — on-session (3-D Secure), optionally on another saved card. Linked from the usage dunning
/// emails. A charge is found by its reference AND the customer's tenant + email claims: anyone else's is a 404.
/// </summary>
public static class UsageChargeEndpoints
{
    public static IEndpointRouteBuilder MapUsageCharges(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me/usage-charges", Mine).WithTags("Usage charges")
            .WithSummary("The signed-in customer's usage charges that are owed (past due or abandoned)")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy);
        app.MapGet("/me/usage-charges/{reference}", MineDetail).WithTags("Usage charges")
            .WithSummary("One of the signed-in customer's usage charges with what it owes")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy);
        app.MapPost("/me/usage-charges/{reference}/pay", Pay).WithTags("Usage charges")
            .WithSummary("Pay an unpaid usage charge now (Idempotency-Key required)")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy);

        // Dev/test only: record + charge a usage charge with a LocalMock scenario (Requires3ds / DeclinedCard …) so the pay
        // page has a past-due charge to show in E2E.
        app.MapPost("/dev/usage-charges", DevCharge).WithTags("Dev").ExcludeFromDescription();
        return app;
    }

    private static async Task<Results<Ok<List<MyUsageChargeDto>>, UnauthorizedHttpResult>> Mine(
        ClaimsPrincipal user, UsageChargeBilling billing, CancellationToken ct)
    {
        if (!CustomerClaims.TryRead(user, out var tenantId, out var email))
        {
            return TypedResults.Unauthorized();
        }

        var list = new List<MyUsageChargeDto>();
        foreach (var charge in await billing.ListPayableForCustomerAsync(tenantId, email, ct))
        {
            list.Add(ToDto(charge, await billing.AmountDueAsync(charge, ct)));
        }

        return TypedResults.Ok(list);
    }

    private static async Task<Results<Ok<MyUsageChargeDto>, NotFound, UnauthorizedHttpResult>> MineDetail(
        string reference, ClaimsPrincipal user, UsageChargeBilling billing, CancellationToken ct)
    {
        if (!CustomerClaims.TryRead(user, out var tenantId, out var email))
        {
            return TypedResults.Unauthorized();
        }

        var charge = await billing.GetForCustomerAsync(tenantId, email, reference, ct);
        return charge is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(charge, await billing.AmountDueAsync(charge, ct)));
    }

    private static async Task<Results<Ok<PayUsageChargeResponse>, NotFound, UnauthorizedHttpResult, ProblemHttpResult>> Pay(
        string reference, PayUsageChargeRequest request, HttpContext http, ClaimsPrincipal user, UsageChargeBilling billing,
        IIdempotencyGuard idempotency, CancellationToken ct)
    {
        if (!CustomerClaims.TryRead(user, out var tenantId, out var email) || !Guid.TryParse(user.FindFirstValue("sub"), out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            return TypedResults.Problem("Idempotency-Key header is required.", statusCode: StatusCodes.Status400BadRequest, title: "Idempotency-Key required");
        }

        if (await billing.GetForCustomerAsync(tenantId, email, reference, ct) is null)
        {
            return TypedResults.NotFound(); // not yours (or none) — 404, never 403
        }

        // A replayed key returns the stored answer (a double submit never pays twice). The client secret of a 3-D Secure
        // answer is handed out once and never stored.
        string? clientSecret = null;
        var response = await idempotency.ExecuteAsync(
            $"usage-charge-pay:{reference}:{key}",
            new { reference, request.SavedPaymentMethodId },
            async token =>
            {
                var result = await billing.PayAsync(tenantId, email, userId, reference, request.SavedPaymentMethodId, token)
                    ?? throw new InvalidOperationException($"usage charge {reference} vanished during payment");
                clientSecret = result.ClientSecret;
                return new PayUsageChargeResponse(result.Outcome, result.PaymentIntentId, result.Charge.Status.ToString(), null);
            },
            ct);

        return response.Outcome switch
        {
            UsageChargePayOutcome.NotPayable => TypedResults.Problem(
                "This usage charge has nothing to pay.", statusCode: StatusCodes.Status409Conflict, title: "Usage charge not payable",
                extensions: new Dictionary<string, object?> { ["outcome"] = (int)response.Outcome }),
            UsageChargePayOutcome.UnknownPaymentMethod => TypedResults.Problem(
                "That card is not one of your saved cards.", statusCode: StatusCodes.Status422UnprocessableEntity, title: "Unknown payment method",
                extensions: new Dictionary<string, object?> { ["outcome"] = (int)response.Outcome }),
            _ => TypedResults.Ok(response with { ClientSecret = clientSecret }),
        };
    }

    private static async Task<Results<Ok<MyUsageChargeDto>, NotFound>> DevCharge(
        DevUsageChargeRequest request, IHostEnvironment env, UsageChargeBilling billing, IConfiguration config, CancellationToken ct)
    {
        if (!env.IsDevelopment())
        {
            return TypedResults.NotFound();
        }

        var tenantId = request.TenantId
            ?? (Guid.TryParse(config["Tenancy:DefaultTenantId"], out var configured) ? configured : new Guid("00000000-0000-0000-0000-000000000001"));
        var reference = $"{(request.Kind == UsageChargeKind.AutoLoad ? "autoload" : "overage")}-dev-{Guid.CreateVersion7():N}";
        var quantity = Math.Max(1, request.Quantity);
        await billing.ChargeAsync(
            new UsageChargeRequest(
                tenantId, request.StorefrontId, request.CustomerEmail, request.Kind, request.Meter, quantity, request.AmountMinor / quantity,
                request.AmountMinor, request.Currency, reference, $"Usage {request.Kind} — {request.Meter} × {quantity}"),
            ct, request.Scenario);
        var charge = await billing.GetForCustomerAsync(tenantId, request.CustomerEmail, reference, ct);
        return charge is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(charge, await billing.AmountDueAsync(charge, ct)));
    }

    private static MyUsageChargeDto ToDto(UsageCharge c, long amountDueMinor) =>
        new(c.Reference, c.Kind, c.Meter, c.Quantity, c.PriceMinor, c.Currency, c.Status.ToString(),
            c.Status is UsageChargeStatus.PastDue or UsageChargeStatus.Abandoned ? amountDueMinor : null,
            c.LastFailureReason, c.NextRetryAt, c.Status == UsageChargeStatus.PastDue ? c.AbandonsAfter : null, c.CreatedAt);
}

/// <summary>
/// A customer's usage charge. <c>Kind</c> (1 Overage, 2 AutoLoad) and <c>Meter</c> cross as NUMBERS; <c>Status</c> is
/// Pending / Settled / PastDue / Abandoned / Skipped; <c>AmountDueMinor</c> (the pinned charge, price + any added tax) only
/// while it is owed; <c>FailureReason</c> RequiresAction / Failed / Cancelled / Error; <c>AbandonsAfter</c> only while past due.
/// </summary>
public record MyUsageChargeDto(
    string Reference, UsageChargeKind Kind, MeterType Meter, long Quantity, long PriceMinor, string Currency, string Status,
    long? AmountDueMinor, string? FailureReason, DateTimeOffset? NextRetryAt, DateTimeOffset? AbandonsAfter, DateTimeOffset CreatedAt);

/// <summary>Pay now, optionally on another of the customer's saved cards (else the card the charge is made on).</summary>
public record PayUsageChargeRequest(Guid? SavedPaymentMethodId);

/// <summary>
/// The pay attempt: <c>Outcome</c> crosses as a NUMBER (1 Paid, 2 RequiresAction, 3 Declined, 4 ProviderUnavailable);
/// <c>Status</c> is the charge's status after it. On RequiresAction, <c>PaymentIntentId</c> + <c>ClientSecret</c> let the
/// client confirm it (3-D Secure); the provider's success event then settles the charge once. 409 nothing to pay, 422 not
/// your card.
/// </summary>
public record PayUsageChargeResponse(UsageChargePayOutcome Outcome, string? PaymentIntentId, string Status, string? ClientSecret);

/// <summary>Dev only: a usage charge for <paramref name="CustomerEmail"/> charged with a LocalMock scenario.</summary>
public record DevUsageChargeRequest(
    Guid? TenantId, [property: Required, EmailAddress] string CustomerEmail, Guid? StorefrontId, UsageChargeKind Kind, MeterType Meter,
    long Quantity, [property: Range(1, long.MaxValue)] long AmountMinor, string Currency, MockScenario? Scenario);
