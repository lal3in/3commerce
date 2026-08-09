using System.Security.Claims;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.BuildingBlocks.Contracts.Support;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Support.Domain;
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
        group.MapPost("/{id:guid}/disposition", SetDisposition)
            .WithSummary("Record/update the disposition of a received return: restock, or storage (with a reason + comments).");
        return app;
    }

    private static async Task<Ok<List<RmaDto>>> ListRmas(string? state, SupportDbContext db, CancellationToken ct)
    {
        var query = db.Rmas.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(state))
        {
            query = query.Where(r => r.CurrentState == state);
        }

        var rmas = await query.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
        var ids = rmas.Select(r => r.CorrelationId).ToList();
        var dispositions = await db.RmaDispositions.AsNoTracking()
            .Where(d => ids.Contains(d.RmaId)).ToDictionaryAsync(d => d.RmaId, ct);
        return TypedResults.Ok(rmas.Select(r =>
        {
            dispositions.TryGetValue(r.CorrelationId, out var d);
            return new RmaDto(
                r.CorrelationId, r.OrderId, r.Email, r.AmountMinor, r.Reason, r.CurrentState, r.CreatedAt,
                r.ReturnReceivedAt, d?.Kind.ToString(), d?.StorageReason?.ToString(), d?.Comments);
        }).ToList());
    }

    // Admin opens an RMA for a whole order straight from the Orders screen, so it travels the single
    // RMA/refund path AND appears in the RMA queue (not a side-channel refund the queue never saw):
    //   AutoApprove=true  (default) → instant, no-return refund → RefundPending → RefundIssued.
    //   AutoApprove=false           → a customer-style Requested RMA the operator then walks through
    //                                 Approve/Approve+Return → AwaitingReturn → Received → refund, so the
    //                                 whole lifecycle is drivable from the admin without a storefront round-trip.
    private static async Task<Results<Accepted<RmaDto>, NotFound, BadRequest<string>>> CreateAdminRefund(
        AdminRefundRequest request, SupportDbContext db, IPublishEndpoint publisher,
        IAuditRecorder audit, ClaimsPrincipal user, IConfiguration config, CancellationToken ct)
    {
        var snapshot = await db.OrderSnapshots.AsNoTracking().Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.OrderId == request.OrderId, ct);
        if (snapshot is null)
        {
            return TypedResults.NotFound();
        }

        var rmaId = Guid.CreateVersion7();

        // Per-line partial refunds for admins (mirrors the customer flow): an empty selection means the
        // whole still-refundable order (the historical admin behaviour); otherwise the chosen lines, each
        // capped at the still-refundable quantity. The amount is server-derived from the snapshot's line
        // prices; Payments prorates tax + shipping on it (ExecuteRefundConsumer). Recording the lines also
        // lets partial returns restock the right quantities and keeps the refundable-units math honest.
        var consumed = await TicketEndpoints.ConsumedQuantitiesAsync(db, request.OrderId, ct);
        int Remaining(OrderSnapshotLine l) => Math.Max(0, l.Quantity - consumed.GetValueOrDefault(l.ProductId));

        var selections = request.Lines is null or { Count: 0 }
            ? snapshot.Lines.Select(l => new RmaLineSelection(l.ProductId, Remaining(l)))
            : request.Lines;

        long amount = 0;
        var recordLines = new List<RmaRequestLine>();
        foreach (var sel in selections)
        {
            var line = snapshot.Lines.FirstOrDefault(l => l.ProductId == sel.ProductId);
            if (line is null)
            {
                return TypedResults.BadRequest("Unknown line in selection.");
            }

            var qty = Math.Clamp(sel.Quantity, 0, Remaining(line));
            if (qty <= 0)
            {
                continue;
            }

            amount += line.UnitPriceMinor * qty;
            recordLines.Add(new RmaRequestLine
            {
                Id = Guid.CreateVersion7(),
                RmaId = rmaId,
                ProductId = line.ProductId,
                Title = line.Title,
                Quantity = qty,
                UnitPriceMinor = line.UnitPriceMinor,
            });
        }

        if (amount <= 0)
        {
            return TypedResults.BadRequest("Nothing left to refund on this order.");
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason)
            ? (request.AutoApprove ? "refunded by admin" : "return requested by admin")
            : request.Reason.Trim();

        db.RmaRequests.Add(new RmaRequestRecord
        {
            Id = rmaId,
            OrderId = request.OrderId,
            Email = snapshot.Email,
            Reason = reason,
            AmountMinor = amount,
            Currency = snapshot.Currency,
            CreatedAt = DateTimeOffset.UtcNow,
            Lines = recordLines,
        });
        await publisher.Publish(new RmaRequested(rmaId, request.OrderId, snapshot.Email, amount, reason, request.AutoApprove), ct);
        var action = request.AutoApprove ? "support.rma.admin_refund" : "support.rma.admin_return";
        await audit.RecordAsync(user.Mutation(DefaultTenantId(config), "Rma", rmaId.ToString(), action, reason), ct);
        await db.SaveChangesAsync(ct);
        var state = request.AutoApprove ? "RefundPending" : "Requested";
        return TypedResults.Accepted((string?)null, new RmaDto(rmaId, request.OrderId, snapshot.Email, amount, reason, state, DateTimeOffset.UtcNow));
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

    // Disposition of a received return (post-receipt, independent of the refund): Restock puts goods
    // back to sellable stock (idempotent RestockRequested); Storage records a reason + comments and does
    // NOT restock. Editable — re-POST to correct the kind/reason/note. Requires the return to be received.
    private static async Task<Results<Ok<RmaDispositionDto>, NotFound, Conflict<string>, BadRequest<string>>> SetDisposition(
        Guid id, DispositionRequest request, SupportDbContext db, IPublishEndpoint publisher,
        IAuditRecorder audit, ClaimsPrincipal user, IConfiguration config, TimeProvider time, CancellationToken ct)
    {
        var rma = await db.Rmas.AsNoTracking().SingleOrDefaultAsync(r => r.CorrelationId == id, ct);
        if (rma is null)
        {
            return TypedResults.NotFound();
        }

        if (rma.ReturnReceivedAt is null)
        {
            return TypedResults.Conflict("Mark the return received before recording a disposition.");
        }

        if (!Enum.TryParse<RmaDispositionKind>(request.Kind, ignoreCase: true, out var kind))
        {
            return TypedResults.BadRequest("Unknown disposition kind.");
        }

        RmaStorageReason? storageReason = null;
        if (kind == RmaDispositionKind.Storage)
        {
            if (!Enum.TryParse<RmaStorageReason>(request.StorageReason, ignoreCase: true, out var parsed))
            {
                return TypedResults.BadRequest("Storage requires a reason: Damage, Incomplete, or UnfitForSale.");
            }

            storageReason = parsed;
        }

        var now = time.GetUtcNow();
        var disposition = await db.RmaDispositions.SingleOrDefaultAsync(d => d.RmaId == id, ct);
        if (disposition is null)
        {
            disposition = new RmaDisposition { RmaId = id, CreatedAt = now, Revision = 1 };
            db.RmaDispositions.Add(disposition);
        }
        else
        {
            disposition.UpdatedAt = now;
            // Every edit bumps the revision so the Payments-side correction is idempotency-distinct: an
            // edit that flips Restock↔Storage reverses the previous revision's posting before the new one.
            disposition.Revision += 1;
        }

        disposition.Kind = kind;
        disposition.StorageReason = storageReason;
        disposition.Comments = string.IsNullOrWhiteSpace(request.Comments) ? null : request.Comments.Trim();

        // Restock returns goods to sellable inventory (idempotent by RMA id downstream); storage records only.
        if (kind == RmaDispositionKind.Restock && request.Restock is { Count: > 0 } lines && request.TenantId is { } tenant)
        {
            await publisher.Publish(new RestockRequested(tenant, id,
                lines.Select(l => new RestockItemInfo(l.ProductId, l.VariantId, l.LocationId, l.Quantity)).ToList()), ct);
        }

        // Hand off to Ordering (the owner of cost knowledge) to value the returned goods and let Payments
        // correct the COGS accrual (phase 1). Support knows only the whole-order RMA + refunded gross, so
        // it carries the OrderId + RefundedMinor and Ordering scales COGS by the refunded share. The enums
        // ride numeric (AGENTS.md); the revision makes each edit's correction idempotency-distinct.
        await publisher.Publish(new RmaDispositionSet(
            id, rma.OrderId, (int)kind, (int?)storageReason, disposition.Revision, rma.AmountMinor), ct);

        var action = kind == RmaDispositionKind.Restock ? "support.rma.disposition.restock" : "support.rma.disposition.storage";
        await audit.RecordAsync(user.Mutation(
            request.TenantId ?? DefaultTenantId(config), "Rma", id.ToString(), action,
            kind == RmaDispositionKind.Storage ? storageReason?.ToString() : null), ct);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new RmaDispositionDto(
            kind.ToString(), storageReason?.ToString(), disposition.Comments, disposition.CreatedAt, disposition.UpdatedAt));
    }

    // The RMA saga carries no tenant, so entries land under the configured default tenant — the same
    // default the Audit search (and Mission Control) falls back to.
    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");
}

public record AdminRefundRequest(Guid OrderId, string? Reason, bool AutoApprove = true, List<RmaLineSelection>? Lines = null);
public record ApproveRequest(bool RequireReturn);
public record RmaDto(
    Guid Id, Guid OrderId, string? Email, long AmountMinor, string? Reason, string State, DateTimeOffset CreatedAt,
    DateTimeOffset? ReturnReceivedAt = null, string? DispositionKind = null, string? StorageReason = null, string? DispositionComments = null);
public record ReturnReceivedRequest(Guid? TenantId, List<RestockLineRequest>? Restock);
public record RestockLineRequest(Guid ProductId, Guid? VariantId, Guid LocationId, int Quantity);
public record DispositionRequest(string Kind, string? StorageReason, string? Comments, Guid? TenantId, List<RestockLineRequest>? Restock);
public record RmaDispositionDto(string Kind, string? StorageReason, string? Comments, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);
