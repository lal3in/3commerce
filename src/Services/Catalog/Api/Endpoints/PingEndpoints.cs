using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Contracts.Ping;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;

namespace ThreeCommerce.Catalog.Api.Endpoints;

/// <summary>
/// Phase-1 spine endpoint. This is the canonical write-and-publish template:
/// entity row and outbox message commit in ONE transaction via SaveChanges.
/// </summary>
public static class PingEndpoints
{
    public static IEndpointRouteBuilder MapPing(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/ping").WithTags("Ping");
        group.MapPost("/", SendPing);
        return app;
    }

    private static async Task<Created<PingResponse>> SendPing(
        CatalogDbContext db,
        IPublishEndpoint publisher,
        CancellationToken cancellationToken)
    {
        var record = new PingRecord { Id = Guid.CreateVersion7(), RequestedAt = DateTimeOffset.UtcNow };

        db.Pings.Add(record);
        await publisher.Publish(new PingRequested(record.Id, record.RequestedAt), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/ping/{record.Id}", new PingResponse(record.Id));
    }
}

public record PingResponse(Guid PingId);
