using System.ComponentModel.DataAnnotations;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Fulfillment.Domain;
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
        return app;
    }

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
