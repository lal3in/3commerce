using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api.Endpoints;

/// <summary>Scheduled-job run history (mt6_3): operator visibility into the recurring jobs.</summary>
public static class JobRunEndpoints
{
    public static IEndpointRouteBuilder MapJobRuns(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/jobs/runs", async (string? job, IJobRunStore store, CancellationToken ct) =>
            {
                var runs = await store.RecentAsync(job, 50, ct);
                return TypedResults.Ok(runs.Select(r => new JobRunDto(
                    r.Id, r.JobName, r.Status.ToString(), r.StartedAt, r.CompletedAt, r.Error)).ToList());
            })
            .WithTags("Jobs")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        return app;
    }
}

public record JobRunDto(Guid Id, string JobName, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Error);

/// <summary>Subscriptions (mt7_3): operator visibility + renew / cancel. Renewals charge via the rail.</summary>
public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptions(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/subscriptions").WithTags("Subscriptions")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Detail);
        group.MapPost("/{id:guid}/renew", Renew);
        group.MapPost("/{id:guid}/cancel", Cancel);

        // Customer "my subscriptions" (mt7_6): scoped to the signed-in customer's tenant + email claims.
        app.MapGet("/me/subscriptions", Mine).WithTags("Subscriptions")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy);

        // The storefront "complete payment" page (owner decision 2026-10-27, ADR-0057 amendment): what a past-due
        // renewal owes, and the customer's own attempt to pay it (on-session — 3-D Secure — optionally on another card).
        app.MapGet("/me/subscriptions/{id:guid}", MineDetail).WithTags("Subscriptions")
            .WithSummary("The signed-in customer's subscription with what its current period owes")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy);
        app.MapPost("/me/subscriptions/{id:guid}/pay", Pay).WithTags("Subscriptions")
            .WithSummary("Pay a past-due renewal now (Idempotency-Key required)")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy);

        // Dev/test only: renew with a LocalMock scenario (Requires3ds / DeclinedCard …) to put a subscription past due.
        app.MapPost("/dev/subscriptions/{id:guid}/renew", DevRenew).WithTags("Dev").ExcludeFromDescription();
        return app;
    }

    private static async Task<Results<Ok<MySubscriptionDto>, NotFound, UnauthorizedHttpResult>> MineDetail(
        Guid id, ClaimsPrincipal user, SubscriptionService subscriptions, CancellationToken ct)
    {
        if (!CustomerClaims.TryRead(user, out var tenantId, out var email))
        {
            return TypedResults.Unauthorized();
        }

        var subscription = await subscriptions.GetForCustomerAsync(tenantId, email, id, ct);
        return subscription is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(await ToMineAsync(subscription, subscriptions, ct));
    }

    private static async Task<Results<Ok<PaySubscriptionResponse>, NotFound, UnauthorizedHttpResult, ProblemHttpResult>> Pay(
        Guid id, PaySubscriptionRequest request, HttpContext http, ClaimsPrincipal user, SubscriptionService subscriptions,
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

        if (await subscriptions.GetForCustomerAsync(tenantId, email, id, ct) is null)
        {
            return TypedResults.NotFound(); // not yours (or none) — 404, never 403
        }

        // A replayed key returns the stored answer (a double submit never pays twice). The client secret of a 3-D
        // Secure answer is handed out once and never stored.
        string? clientSecret = null;
        var response = await idempotency.ExecuteAsync(
            $"subscription-pay:{id}:{key}",
            new { id, request.SavedPaymentMethodId },
            async token =>
            {
                var result = await subscriptions.PayAsync(tenantId, email, userId, id, request.SavedPaymentMethodId, token)
                    ?? throw new InvalidOperationException($"subscription {id} vanished during payment");
                clientSecret = result.ClientSecret;
                return new PaySubscriptionResponse(
                    result.Outcome, result.PaymentIntentId, result.Subscription.Status.ToString(), null);
            },
            ct);

        return response.Outcome switch
        {
            SubscriptionPayOutcome.NotPastDue => TypedResults.Problem(
                "This subscription has nothing to pay.", statusCode: StatusCodes.Status409Conflict, title: "Subscription not past due",
                extensions: new Dictionary<string, object?> { ["outcome"] = (int)response.Outcome }),
            SubscriptionPayOutcome.UnknownPaymentMethod => TypedResults.Problem(
                "That card is not one of your saved cards.", statusCode: StatusCodes.Status422UnprocessableEntity, title: "Unknown payment method",
                extensions: new Dictionary<string, object?> { ["outcome"] = (int)response.Outcome }),
            _ => TypedResults.Ok(response with { ClientSecret = clientSecret }),
        };
    }

    private static async Task<Results<Ok<SubscriptionDto>, NotFound>> DevRenew(
        Guid id, Guid? tenantId, MockScenario? scenario, IHostEnvironment env, SubscriptionService subscriptions, IConfiguration config,
        CancellationToken ct)
    {
        if (!env.IsDevelopment())
        {
            return TypedResults.NotFound();
        }

        var subscription = await subscriptions.RenewAsync(tenantId ?? DefaultTenantId(config), id, ct, scenario);
        return subscription is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(subscription));
    }

    private static async Task<MySubscriptionDto> ToMineAsync(Subscription s, SubscriptionService subscriptions, CancellationToken ct) =>
        new(s.Id, s.OrderId, s.ProductId, s.VariantId, s.BillingPeriod.ToString(), s.PriceMinor, s.Quantity, s.Currency, s.Status.ToString(),
            s.CurrentPeriodStart, s.CurrentPeriodEnd,
            s.Status == SubscriptionStatus.PastDue ? await subscriptions.AmountDueAsync(s, ct) ?? s.PriceMinor : null,
            s.LastFailureReason, s.NextRetryAt, s.CancelsAfter);

    private static async Task<Results<Ok<List<SubscriptionDto>>, UnauthorizedHttpResult>> Mine(
        ClaimsPrincipal user, SubscriptionService subscriptions, CancellationToken ct)
    {
        if (!CustomerClaims.TryRead(user, out var tenantId, out var email))
        {
            return TypedResults.Unauthorized();
        }

        var list = await subscriptions.ListAsync(tenantId, email, ct);
        return TypedResults.Ok(list.Select(ToDto).ToList());
    }

    private static async Task<Ok<List<SubscriptionDto>>> List(
        Guid? tenantId, string? email, SubscriptionService subscriptions, IConfiguration config, CancellationToken ct)
    {
        var list = await subscriptions.ListAsync(tenantId ?? DefaultTenantId(config), email, ct);
        return TypedResults.Ok(list.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<SubscriptionDetailDto>, NotFound>> Detail(
        Guid id, Guid? tenantId, SubscriptionService subscriptions, IConfiguration config, CancellationToken ct)
    {
        var subscription = await subscriptions.GetAsync(tenantId ?? DefaultTenantId(config), id, ct);
        return subscription is null ? TypedResults.NotFound() : TypedResults.Ok(ToDetailDto(subscription));
    }

    private static async Task<Results<Ok<SubscriptionDto>, NotFound>> Renew(
        Guid id, Guid? tenantId, SubscriptionService subscriptions, PaymentsDbContext db, IConfiguration config,
        IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct)
    {
        var subscription = await subscriptions.RenewAsync(tenantId ?? DefaultTenantId(config), id, ct);
        if (subscription is null)
        {
            return TypedResults.NotFound();
        }

        // Timeline visibility (mc1): a manual renew records an audit event carrying the resulting status, so
        // both a successful renewal and a dunning past-due land on Mission Control's activity timeline.
        // RenewAsync already saved the renewal, so the audit publish is flushed by our own SaveChanges here
        // (same scoped DbContext + bus outbox — publishing without a following Save would strand the row).
        await audit.RecordAsync(user.Mutation(
            subscription.TenantId, "Subscription", subscription.Id.ToString(), "payments.subscription.renew", subscription.Status.ToString()), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(subscription));
    }

    private static async Task<Results<Ok<SubscriptionDto>, NotFound>> Cancel(
        Guid id, Guid? tenantId, SubscriptionService subscriptions, PaymentsDbContext db, IConfiguration config,
        IAuditRecorder audit, ClaimsPrincipal user, CancellationToken ct)
    {
        var subscription = await subscriptions.CancelAsync(tenantId ?? DefaultTenantId(config), id, ct);
        if (subscription is null)
        {
            return TypedResults.NotFound();
        }

        await audit.RecordAsync(user.Mutation(
            subscription.TenantId, "Subscription", subscription.Id.ToString(), "payments.subscription.cancel", subscription.Status.ToString()), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(subscription));
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static SubscriptionDto ToDto(Subscription s) =>
        new(s.Id, s.OrderId, s.StorefrontId, s.CustomerEmail, s.ProductId, s.VariantId, s.BillingPeriod.ToString(), s.PriceMinor, s.Currency,
            s.Status.ToString(), s.CurrentPeriodStart, s.CurrentPeriodEnd, s.CreatedAt, s.UpdatedAt, s.Quantity,
            s.LastFailureReason, s.NextRetryAt, s.CancelsAfter);

    private static SubscriptionDetailDto ToDetailDto(Subscription s) =>
        new(s.Id, s.TenantId, s.OrderId, s.StorefrontId, s.CustomerEmail, s.ProductId, s.VariantId, s.BillingPeriod.ToString(), s.PriceMinor,
            s.Currency, s.Status.ToString(), s.CurrentPeriodStart, s.CurrentPeriodEnd, s.CreatedAt, s.UpdatedAt,
            s.Renewals.OrderBy(r => r.Sequence)
                .Select(r => new RenewalDto(r.Sequence, r.PeriodStart, r.PeriodEnd, r.AmountMinor, r.Currency, r.RecordedAt))
                .ToList(),
            s.Quantity, s.LastFailureReason, s.NextRetryAt, s.CancelsAfter);
}

/// <summary>A subscription. <c>PriceMinor</c> is each renewal's price for all <c>Quantity</c> units (appended, ADR-0057 amendment 2026-10-27).</summary>
public record SubscriptionDto(
    Guid Id, Guid OrderId, Guid? StorefrontId, string CustomerEmail, Guid ProductId, Guid? VariantId, string BillingPeriod, long PriceMinor, string Currency,
    string Status, DateTimeOffset CurrentPeriodStart, DateTimeOffset CurrentPeriodEnd, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    int Quantity = 1, string? FailureReason = null, DateTimeOffset? NextRetryAt = null, DateTimeOffset? CancelsAfter = null);

public record RenewalDto(int Sequence, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, long AmountMinor, string Currency, DateTimeOffset RecordedAt);

public record SubscriptionDetailDto(
    Guid Id, Guid TenantId, Guid OrderId, Guid? StorefrontId, string CustomerEmail, Guid ProductId, Guid? VariantId, string BillingPeriod, long PriceMinor,
    string Currency, string Status, DateTimeOffset CurrentPeriodStart, DateTimeOffset CurrentPeriodEnd, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, IReadOnlyList<RenewalDto> Renewals, int Quantity = 1,
    string? FailureReason = null, DateTimeOffset? NextRetryAt = null, DateTimeOffset? CancelsAfter = null);

/// <summary>
/// The customer's own subscription for the "complete payment" page. <c>AmountDueMinor</c> (past due only) is what the
/// unpaid period charges — the pinned price + any added tax, the same for every attempt. <c>FailureReason</c>:
/// RequiresAction / Failed / Cancelled / Error. <c>NextRetryAt</c>: the next scheduled retry; <c>CancelsAfter</c>: when
/// an unpaid period cancels the subscription.
/// </summary>
public record MySubscriptionDto(
    Guid Id, Guid OrderId, Guid ProductId, Guid? VariantId, string BillingPeriod, long PriceMinor, int Quantity, string Currency, string Status,
    DateTimeOffset CurrentPeriodStart, DateTimeOffset CurrentPeriodEnd, long? AmountDueMinor, string? FailureReason,
    DateTimeOffset? NextRetryAt, DateTimeOffset? CancelsAfter);

/// <summary>Optionally pay with (and from now on renew on) another of the customer's saved cards.</summary>
public record PaySubscriptionRequest(Guid? SavedPaymentMethodId);

/// <summary>
/// The pay attempt: <c>Outcome</c> crosses as a NUMBER (1 Paid, 2 RequiresAction, 3 Declined, 4 ProviderUnavailable);
/// <c>Status</c> is the subscription's status after it. On RequiresAction, <c>PaymentIntentId</c> + <c>ClientSecret</c>
/// let the client complete 3-D Secure (the secret is returned once, never on an idempotent replay).
/// </summary>
public record PaySubscriptionResponse(SubscriptionPayOutcome Outcome, string? PaymentIntentId, string Status, string? ClientSecret);
