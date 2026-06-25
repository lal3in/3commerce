using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
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
        group.MapPost("/{id:guid}/renew", Renew);
        group.MapPost("/{id:guid}/cancel", Cancel);

        // Customer "my subscriptions" (mt7_6): scoped to the signed-in customer's tenant + email claims.
        app.MapGet("/me/subscriptions", Mine).WithTags("Subscriptions")
            .RequireAuthorization(InternalClaimsAuth.CustomerPolicy);
        return app;
    }

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

    private static async Task<Results<Ok<SubscriptionDto>, NotFound>> Renew(
        Guid id, Guid? tenantId, SubscriptionService subscriptions, IConfiguration config, CancellationToken ct)
    {
        var subscription = await subscriptions.RenewAsync(tenantId ?? DefaultTenantId(config), id, ct);
        return subscription is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(subscription));
    }

    private static async Task<Results<Ok<SubscriptionDto>, NotFound>> Cancel(
        Guid id, Guid? tenantId, SubscriptionService subscriptions, IConfiguration config, CancellationToken ct)
    {
        var subscription = await subscriptions.CancelAsync(tenantId ?? DefaultTenantId(config), id, ct);
        return subscription is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(subscription));
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static SubscriptionDto ToDto(Subscription s) =>
        new(s.Id, s.OrderId, s.CustomerEmail, s.ProductId, s.VariantId, s.BillingPeriod.ToString(), s.PriceMinor, s.Currency,
            s.Status.ToString(), s.CurrentPeriodStart, s.CurrentPeriodEnd);
}

public record SubscriptionDto(
    Guid Id, Guid OrderId, string CustomerEmail, Guid ProductId, Guid? VariantId, string BillingPeriod, long PriceMinor, string Currency,
    string Status, DateTimeOffset CurrentPeriodStart, DateTimeOffset CurrentPeriodEnd);
