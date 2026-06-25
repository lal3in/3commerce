using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api.Endpoints;

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
        return app;
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
