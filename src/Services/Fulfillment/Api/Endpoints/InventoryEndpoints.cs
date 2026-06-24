using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.Fulfillment.Api.Endpoints;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventory(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/inventory").WithTags("Inventory")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapPost("/locations", CreateLocation);
        group.MapGet("/locations", ListLocations);
        // Stock feed — admins and supplier feeds set on-hand; dispatch/tracking stays admin-only (v1).
        group.MapPost("/stock", SetStock);
        group.MapGet("/stock", ListStock);
        // Manual restock of returned items (mt4_8) — operator-driven, partial allowed.
        group.MapPost("/restock", Restock);
        return app;
    }

    private static async Task<Ok> Restock(
        RestockRequest request, ReservationService reservations, IConfiguration config, CancellationToken ct)
    {
        var lines = request.Items
            .Select(i => new RestockLine(i.ProductId, i.VariantId, i.LocationId, i.Quantity))
            .ToList();
        await reservations.RestockAsync(request.TenantId ?? DefaultTenantId(config), request.ReferenceId, lines, ct);
        return TypedResults.Ok();
    }

    private static async Task<Results<Created<LocationDto>, BadRequest<string>>> CreateLocation(
        CreateLocationRequest request, InventoryService inventory, IConfiguration config, CancellationToken ct)
    {
        try
        {
            var location = await inventory.CreateLocationAsync(
                request.TenantId ?? DefaultTenantId(config), request.EntityId, request.AddressId,
                request.Name, request.Kind, ct);
            return TypedResults.Created($"/admin/inventory/locations/{location.Id}", ToDto(location));
        }
        catch (FulfillmentRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Ok<List<LocationDto>>> ListLocations(
        Guid? tenantId, InventoryService inventory, IConfiguration config, CancellationToken ct)
    {
        var locations = await inventory.ListLocationsAsync(tenantId ?? DefaultTenantId(config), ct);
        return TypedResults.Ok(locations.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<StockDto>, BadRequest<string>>> SetStock(
        SetStockRequest request, InventoryService inventory, IConfiguration config, CancellationToken ct)
    {
        try
        {
            var item = await inventory.SetStockAsync(
                request.TenantId ?? DefaultTenantId(config), request.LocationId, request.ProductId,
                request.VariantId, request.OnHand, ct);
            return TypedResults.Ok(ToDto(item));
        }
        catch (FulfillmentRuleException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Ok<List<StockDto>>> ListStock(
        Guid? tenantId, Guid? productId, InventoryService inventory, IConfiguration config, CancellationToken ct)
    {
        var items = await inventory.ListStockAsync(tenantId ?? DefaultTenantId(config), productId, ct);
        return TypedResults.Ok(items.Select(ToDto).ToList());
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static LocationDto ToDto(InventoryLocation l) =>
        new(l.Id, l.TenantId, l.EntityId, l.AddressId, l.Name, l.Kind.ToString(), l.Status.ToString());

    private static StockDto ToDto(InventoryItem i) =>
        new(i.Id, i.LocationId, i.ProductId, i.VariantId, i.QuantityOnHand, i.QuantityReserved, i.Available);
}

public record CreateLocationRequest(
    Guid? TenantId,
    [property: Required] Guid EntityId,
    Guid? AddressId,
    [property: Required, MaxLength(200)] string Name,
    LocationKind Kind);

public record SetStockRequest(
    Guid? TenantId,
    [property: Required] Guid LocationId,
    [property: Required] Guid ProductId,
    Guid? VariantId,
    [property: Range(0, int.MaxValue)] int OnHand);

public record LocationDto(Guid Id, Guid TenantId, Guid EntityId, Guid? AddressId, string Name, string Kind, string Status);

public record StockDto(Guid Id, Guid LocationId, Guid ProductId, Guid? VariantId, int OnHand, int Reserved, int Available);

public record RestockRequest(Guid? TenantId, [property: Required] Guid ReferenceId, List<RestockItem> Items);

public record RestockItem(
    [property: Required] Guid ProductId, Guid? VariantId, [property: Required] Guid LocationId,
    [property: Range(1, int.MaxValue)] int Quantity);
