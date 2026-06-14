using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Support.Infrastructure;
using ThreeCommerce.Support.Infrastructure.Sagas;

namespace ThreeCommerce.Support.Api.Endpoints;

public static class AdminRmaEndpoints
{
    public static IEndpointRouteBuilder MapAdminRmas(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/rmas").WithTags("RMA").RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        group.MapGet("/", ListRmas);
        group.MapPost("/{id:guid}/approve", Approve);
        group.MapPost("/{id:guid}/deny", Deny);
        group.MapPost("/{id:guid}/return-received", ReturnReceivedAction);
        return app;
    }

    private static async Task<Ok<List<RmaDto>>> ListRmas(string? state, SupportDbContext db, CancellationToken ct)
    {
        var query = db.Rmas.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(state))
        {
            query = query.Where(r => r.CurrentState == state);
        }

        var rmas = await query.OrderByDescending(r => r.CreatedAt).Take(200)
            .Select(r => new RmaDto(r.CorrelationId, r.OrderId, r.Email, r.AmountMinor, r.Reason, r.CurrentState, r.CreatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(rmas);
    }

    /// <summary>Idempotent: approving an already-approved RMA is a no-op (FR-10).</summary>
    private static async Task<Results<Accepted, Conflict<string>, NotFound>> Approve(
        Guid id, ApproveRequest? request, SupportDbContext db, IPublishEndpoint publisher, CancellationToken ct)
    {
        var rma = await db.Rmas.AsNoTracking().SingleOrDefaultAsync(r => r.CorrelationId == id, ct);
        if (rma is null)
        {
            return TypedResults.NotFound();
        }

        if (rma.CurrentState != "Requested")
        {
            // Already past Requested → no-op for approve, conflict for everything else.
            return TypedResults.Conflict($"RMA is '{rma.CurrentState}', cannot approve.");
        }

        await publisher.Publish(new RmaApproved(id, request?.RequireReturn ?? false), ct);
        await db.SaveChangesAsync(ct); // flush the bus outbox
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<Accepted, Conflict<string>, NotFound>> Deny(
        Guid id, SupportDbContext db, IPublishEndpoint publisher, CancellationToken ct)
    {
        var rma = await db.Rmas.AsNoTracking().SingleOrDefaultAsync(r => r.CorrelationId == id, ct);
        if (rma is null)
        {
            return TypedResults.NotFound();
        }

        if (rma.CurrentState != "Requested")
        {
            return TypedResults.Conflict($"RMA is '{rma.CurrentState}', cannot deny.");
        }

        await publisher.Publish(new RmaDenied(id), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<Accepted, Conflict<string>, NotFound>> ReturnReceivedAction(
        Guid id, SupportDbContext db, IPublishEndpoint publisher, CancellationToken ct)
    {
        var rma = await db.Rmas.AsNoTracking().SingleOrDefaultAsync(r => r.CorrelationId == id, ct);
        if (rma is null)
        {
            return TypedResults.NotFound();
        }

        if (rma.CurrentState != "AwaitingReturn")
        {
            return TypedResults.Conflict($"RMA is '{rma.CurrentState}', not awaiting a return.");
        }

        await publisher.Publish(new ReturnReceived(id), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted((string?)null);
    }
}

public record ApproveRequest(bool RequireReturn);
public record RmaDto(Guid Id, Guid OrderId, string? Email, long AmountMinor, string? Reason, string State, DateTimeOffset CreatedAt);
