using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.Fulfillment.Api.Endpoints;

/// <summary>Customer entitlements (mt7_2): admin visibility of digital access issued on order confirm.</summary>
public static class EntitlementEndpoints
{
    public static IEndpointRouteBuilder MapEntitlements(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/entitlements").WithTags("Entitlements")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        group.MapGet("/", List);
        return app;
    }

    private static async Task<Ok<List<EntitlementDto>>> List(
        Guid? tenantId, Guid? orderId, string? email, EntitlementService entitlements, IConfiguration config, CancellationToken ct)
    {
        var list = await entitlements.ListAsync(tenantId ?? DefaultTenantId(config), orderId, email, ct);
        return TypedResults.Ok(list.Select(ToDto).ToList());
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static EntitlementDto ToDto(Entitlement e) =>
        new(e.Id, e.OrderId, e.CustomerEmail, e.ProductId, e.VariantId, e.Type.ToString(), e.Status.ToString(), e.StartsAt, e.ExpiresAt);
}

public record EntitlementDto(
    Guid Id, Guid OrderId, string CustomerEmail, Guid ProductId, Guid? VariantId, string Type, string Status,
    DateTimeOffset StartsAt, DateTimeOffset? ExpiresAt);
