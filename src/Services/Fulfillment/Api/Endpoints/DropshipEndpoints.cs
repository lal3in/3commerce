using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Fulfillment.Domain.Dropship;
using ThreeCommerce.Fulfillment.Infrastructure.Dropship;

namespace ThreeCommerce.Fulfillment.Api.Endpoints;

/// <summary>Dropship operations (mt4_4b): supplier-order visibility + the supplier availability feed.</summary>
public static class DropshipEndpoints
{
    public static IEndpointRouteBuilder MapDropship(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/dropship").WithTags("Dropship")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/orders", ListOrders);
        // Supplier feed — suppliers update availability (not dispatch/tracking) in v1.
        group.MapPost("/availability", SetAvailability);
        group.MapGet("/availability", ListAvailability);
        return app;
    }

    private static async Task<Ok<List<SupplierOrderDto>>> ListOrders(
        Guid? tenantId, Guid? orderId, SupplierOrderService orders, IConfiguration config, CancellationToken ct)
    {
        var list = await orders.ListAsync(tenantId ?? DefaultTenantId(config), orderId, ct);
        return TypedResults.Ok(list.Select(o => new SupplierOrderDto(
            o.Id, o.OrderId, o.SupplierId, o.Status.ToString(), o.ExternalReference, o.TrackingNumber, o.Carrier, o.FailureReason)).ToList());
    }

    private static async Task<Ok<AvailabilityDto>> SetAvailability(
        SetAvailabilityRequest request, SupplierAvailabilityService availability, IConfiguration config, CancellationToken ct)
    {
        var item = await availability.SetAsync(
            request.TenantId ?? DefaultTenantId(config), request.SupplierId, request.ProductId, request.VariantId,
            request.Status, request.ExternalQuantity, request.SupplierSku, ct);
        return TypedResults.Ok(ToDto(item));
    }

    private static async Task<Ok<List<AvailabilityDto>>> ListAvailability(
        Guid? tenantId, Guid? supplierId, Guid? productId, SupplierAvailabilityService availability, IConfiguration config, CancellationToken ct)
    {
        var list = await availability.ListAsync(tenantId ?? DefaultTenantId(config), supplierId, productId, ct);
        return TypedResults.Ok(list.Select(ToDto).ToList());
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static AvailabilityDto ToDto(SupplierAvailability a) =>
        new(a.Id, a.SupplierId, a.ProductId, a.VariantId, a.SupplierSku, a.Status.ToString(), a.ExternalQuantity, a.IsSellable, a.LastCheckedAt);
}

public record SetAvailabilityRequest(
    Guid? TenantId,
    [property: Required] Guid SupplierId,
    [property: Required] Guid ProductId,
    Guid? VariantId,
    SupplierStockStatus Status,
    int? ExternalQuantity,
    [property: MaxLength(100)] string? SupplierSku);

public record SupplierOrderDto(
    Guid Id, Guid OrderId, Guid SupplierId, string Status, string? ExternalReference, string? TrackingNumber, string? Carrier, string? FailureReason);

public record AvailabilityDto(
    Guid Id, Guid SupplierId, Guid ProductId, Guid? VariantId, string? SupplierSku, string Status, int? ExternalQuantity, bool IsSellable, DateTimeOffset LastCheckedAt);
