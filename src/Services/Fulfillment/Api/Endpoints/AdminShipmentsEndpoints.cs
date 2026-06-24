using System.ComponentModel.DataAnnotations;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.Fulfillment.Api.Endpoints;

public static class AdminShipmentsEndpoints
{
    public static IEndpointRouteBuilder MapAdminShipments(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/shipments").WithTags("Shipments")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List);
        group.MapPost("/{id:guid}/tracking", AssignTracking);
        // Packages, labels, tracking (mt4_7) — automation off; operators drive these manually.
        group.MapPost("/{id:guid}/packages", AddPackage);
        group.MapGet("/{id:guid}/packages", ListPackages);

        var packages = app.MapGroup("/admin/packages").WithTags("Packages")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        packages.MapPost("/{id:guid}/label", BuyLabel);
        packages.MapPost("/{id:guid}/tracking/refresh", RefreshTracking);
        return app;
    }

    private static async Task<Results<Created<PackageDto>, NotFound>> AddPackage(
        Guid id, AddPackageRequest request, ShipmentService shipments, IConfiguration config, CancellationToken ct)
    {
        var package = await shipments.AddPackageAsync(
            request.TenantId ?? DefaultTenantId(config), id,
            new Parcel(request.WeightGrams, request.LengthMm, request.WidthMm, request.HeightMm), ct);
        return package is null
            ? TypedResults.NotFound()
            : TypedResults.Created($"/admin/packages/{package.Id}", ToDto(package));
    }

    private static async Task<Ok<List<PackageDto>>> ListPackages(
        Guid id, Guid? tenantId, ShipmentService shipments, IConfiguration config, CancellationToken ct)
    {
        var list = await shipments.ListPackagesAsync(tenantId ?? DefaultTenantId(config), id, ct);
        return TypedResults.Ok(list.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<PackageDto>, NotFound>> BuyLabel(
        Guid id, BuyLabelRequest request, ShipmentService shipments, IConfiguration config, CancellationToken ct)
    {
        var package = await shipments.BuyLabelAsync(request.TenantId ?? DefaultTenantId(config), id, request.Carrier, ct);
        return package is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(package));
    }

    private static async Task<Results<Ok<PackageDto>, NotFound>> RefreshTracking(
        Guid id, Guid? tenantId, ShipmentService shipments, IConfiguration config, CancellationToken ct)
    {
        var package = await shipments.RefreshTrackingAsync(tenantId ?? DefaultTenantId(config), id, ct);
        return package is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(package));
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static PackageDto ToDto(Package p) =>
        new(p.Id, p.ShipmentId, p.WeightGrams, p.Carrier?.ToString(), p.TrackingNumber, p.LabelUrl, p.Status.ToString());

    private static async Task<Ok<List<ShipmentDto>>> List(
        Guid? orderId, FulfillmentDbContext db, CancellationToken ct)
    {
        var query = db.Shipments.AsNoTracking().Include(s => s.Lines).AsQueryable();
        if (orderId is { } oid)
        {
            query = query.Where(s => s.OrderId == oid);
        }

        var shipments = await query.OrderByDescending(s => s.CreatedAt).Take(200)
            .Select(s => new ShipmentDto(
                s.Id, s.OrderId, s.FulfillmentSource, s.Status.ToString(), s.Carrier, s.TrackingNumber,
                s.Lines.Select(l => new ShipmentLineDto(l.ProductId, l.Title, l.Quantity)).ToList()))
            .ToListAsync(ct);
        return TypedResults.Ok(shipments);
    }

    private static async Task<Results<NoContent, NotFound>> AssignTracking(
        Guid id, TrackingRequest request, FulfillmentDbContext db, IPublishEndpoint publisher, CancellationToken ct)
    {
        var shipment = await db.Shipments.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (shipment is null)
        {
            return TypedResults.NotFound();
        }

        // Idempotent: assigning the same tracking twice emits no second event/email.
        if (shipment.TrackingNumber == request.TrackingNumber && shipment.Status == ShipmentStatus.Dispatched)
        {
            return TypedResults.NoContent();
        }

        shipment.Carrier = request.Carrier;
        shipment.TrackingNumber = request.TrackingNumber;
        shipment.Status = ShipmentStatus.Dispatched;
        await publisher.Publish(new TrackingAssigned(shipment.Id, shipment.OrderId, request.Carrier, request.TrackingNumber), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}

public record TrackingRequest([property: Required] string Carrier, [property: Required] string TrackingNumber);
public record ShipmentLineDto(Guid ProductId, string Title, int Quantity);
public record ShipmentDto(Guid Id, Guid OrderId, string FulfillmentSource, string Status, string? Carrier, string? TrackingNumber, List<ShipmentLineDto> Lines);

public record AddPackageRequest(Guid? TenantId, [property: Range(0, int.MaxValue)] int WeightGrams, int LengthMm, int WidthMm, int HeightMm);
public record BuyLabelRequest(Guid? TenantId, CarrierCode? Carrier);
public record PackageDto(Guid Id, Guid ShipmentId, int WeightGrams, string? Carrier, string? TrackingNumber, string? LabelUrl, string Status);
