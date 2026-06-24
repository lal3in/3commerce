using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.Fulfillment.Api.Endpoints;

/// <summary>Order holds before fulfilment (mt4_9): place / list / release. Releasing the last hold fulfils.</summary>
public static class OrderHoldEndpoints
{
    public static IEndpointRouteBuilder MapOrderHolds(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/admin/orders").WithTags("Holds")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        orders.MapPost("/{orderId:guid}/holds", PlaceHold);
        orders.MapGet("/{orderId:guid}/holds", ListHolds);

        var holds = app.MapGroup("/admin/holds").WithTags("Holds")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        holds.MapPost("/{id:guid}/release", ReleaseHold);
        return app;
    }

    private static async Task<Created<HoldDto>> PlaceHold(
        Guid orderId, PlaceHoldRequest request, OrderHoldService holds, IConfiguration config, CancellationToken ct)
    {
        var hold = await holds.PlaceHoldAsync(
            request.TenantId ?? DefaultTenantId(config), orderId, request.Reason, request.Note, request.PlacedBy, ct);
        return TypedResults.Created($"/admin/holds/{hold.Id}", ToDto(hold));
    }

    private static async Task<Ok<List<HoldDto>>> ListHolds(
        Guid orderId, Guid? tenantId, OrderHoldService holds, IConfiguration config, CancellationToken ct)
    {
        var list = await holds.ListAsync(tenantId ?? DefaultTenantId(config), orderId, ct);
        return TypedResults.Ok(list.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<HoldDto>, NotFound>> ReleaseHold(
        Guid id, Guid? tenantId, OrderHoldService holds, IConfiguration config, CancellationToken ct)
    {
        var hold = await holds.ReleaseHoldAsync(tenantId ?? DefaultTenantId(config), id, ct);
        return hold is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(hold));
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static HoldDto ToDto(OrderHold h) =>
        new(h.Id, h.OrderId, h.Reason.ToString(), h.Status.ToString(), h.Note, h.PlacedBy, h.CreatedAt, h.ReleasedAt);
}

public record PlaceHoldRequest(Guid? TenantId, HoldReason Reason, string? Note, string? PlacedBy);

public record HoldDto(Guid Id, Guid OrderId, string Reason, string Status, string? Note, string? PlacedBy, DateTimeOffset CreatedAt, DateTimeOffset? ReleasedAt);
