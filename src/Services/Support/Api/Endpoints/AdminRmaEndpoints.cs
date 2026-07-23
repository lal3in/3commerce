using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
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
        group.MapPost("/", CreateAdminRefund)
            .WithSummary("Admin-opened RMA for a whole order: auto-approved instant refund (default), or a Requested RMA the operator walks through the lifecycle when AutoApprove=false.");
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

    // Admin opens an RMA for a whole order straight from the Orders screen, so it travels the single
    // RMA/refund path AND appears in the RMA queue (not a side-channel refund the queue never saw):
    //   AutoApprove=true  (default) → instant, no-return refund → RefundPending → RefundIssued.
    //   AutoApprove=false           → a customer-style Requested RMA the operator then walks through
    //                                 Approve/Approve+Return → AwaitingReturn → Received → refund, so the
    //                                 whole lifecycle is drivable from the admin without a storefront round-trip.
    private static async Task<Results<Accepted<RmaDto>, NotFound>> CreateAdminRefund(
        AdminRefundRequest request, SupportDbContext db, IPublishEndpoint publisher,
        IAuditRecorder audit, ClaimsPrincipal user, IConfiguration config, CancellationToken ct)
    {
        var snapshot = await db.OrderSnapshots.AsNoTracking().SingleOrDefaultAsync(o => o.OrderId == request.OrderId, ct);
        if (snapshot is null)
        {
            return TypedResults.NotFound();
        }

        var rmaId = Guid.CreateVersion7();
        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? (request.AutoApprove ? "refunded by admin" : "return requested by admin")
            : request.Reason.Trim();
        await publisher.Publish(new RmaRequested(rmaId, request.OrderId, snapshot.Email, snapshot.GrossMinor, reason, request.AutoApprove), ct);
        var action = request.AutoApprove ? "support.rma.admin_refund" : "support.rma.admin_return";
        await audit.RecordAsync(user.Mutation(DefaultTenantId(config), "Rma", rmaId.ToString(), action, reason), ct);
        await db.SaveChangesAsync(ct);
        var state = request.AutoApprove ? "RefundPending" : "Requested";
        return TypedResults.Accepted((string?)null, new RmaDto(rmaId, request.OrderId, snapshot.Email, snapshot.GrossMinor, reason, state, DateTimeOffset.UtcNow));
    }

    /// <summary>Idempotent: approving an already-approved RMA is a no-op (FR-10).</summary>
    private static async Task<Results<Accepted, Conflict<string>, NotFound>> Approve(
        Guid id, ApproveRequest? request, SupportDbContext db, IPublishEndpoint publisher,
        IAuditRecorder audit, ClaimsPrincipal user, IConfiguration config, CancellationToken ct)
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
        await audit.RecordAsync(user.Mutation(DefaultTenantId(config), "Rma", id.ToString(), "support.rma.approve"), ct);
        await db.SaveChangesAsync(ct); // flush the bus outbox
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<Accepted, Conflict<string>, NotFound>> Deny(
        Guid id, SupportDbContext db, IPublishEndpoint publisher,
        IAuditRecorder audit, ClaimsPrincipal user, IConfiguration config, CancellationToken ct)
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
        await audit.RecordAsync(user.Mutation(DefaultTenantId(config), "Rma", id.ToString(), "support.rma.deny"), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<Accepted, Conflict<string>, NotFound>> ReturnReceivedAction(
        Guid id, ReturnReceivedRequest? request, SupportDbContext db, IPublishEndpoint publisher,
        IAuditRecorder audit, ClaimsPrincipal user, IConfiguration config, CancellationToken ct)
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
        // Manual partial restock (mt4_8): the operator chooses which returned lines go back to stock,
        // and to which location. Fulfillment increments on-hand + records a Returned movement (RMA id = reference).
        if (request?.Restock is { Count: > 0 } restock && request.TenantId is { } tenant)
        {
            await publisher.Publish(new RestockRequested(tenant, id,
                restock.Select(r => new RestockItemInfo(r.ProductId, r.VariantId, r.LocationId, r.Quantity)).ToList()), ct);
        }

        await audit.RecordAsync(user.Mutation(
            request?.TenantId ?? DefaultTenantId(config), "Rma", id.ToString(), "support.rma.return_received"), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted((string?)null);
    }

    // The RMA saga carries no tenant, so entries land under the configured default tenant — the same
    // default the Audit search (and Mission Control) falls back to.
    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");
}

public record AdminRefundRequest(Guid OrderId, string? Reason, bool AutoApprove = true);
public record ApproveRequest(bool RequireReturn);
public record RmaDto(Guid Id, Guid OrderId, string? Email, long AmountMinor, string? Reason, string State, DateTimeOffset CreatedAt);
public record ReturnReceivedRequest(Guid? TenantId, List<RestockLineRequest>? Restock);
public record RestockLineRequest(Guid ProductId, Guid? VariantId, Guid LocationId, int Quantity);
