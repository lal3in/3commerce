using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Infrastructure;

/// <summary>
/// Supplier change-request lifecycle (mt2_7): the portal raises a request; a tenant admin
/// approves or rejects it with maker-checker (the deciding principal differs from the
/// requester, ADR-0025). Applying an approved change stays with the owning service.
/// Maker-checker decisions are written to the local audit log (mt6_1).
/// </summary>
public sealed class SupplierChangeRequestService(EntityDbContext db, AuditRecorder audit, TimeProvider timeProvider)
{
    public async Task<SupplierChangeRequest> OpenAsync(
        Guid tenantId, Guid entityId, SupplierChangeRequestType type, string summary, string? detail, Guid requestedByPrincipalId, CancellationToken cancellationToken)
    {
        var request = SupplierChangeRequest.Open(tenantId, entityId, type, summary, detail, requestedByPrincipalId, timeProvider.GetUtcNow());
        db.SupplierChangeRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    public Task<List<SupplierChangeRequest>> ListAsync(Guid tenantId, SupplierChangeRequestStatus? status, CancellationToken cancellationToken) =>
        db.SupplierChangeRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && (status == null || r.Status == status))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<SupplierChangeRequest?> ApproveAsync(
        Guid tenantId, Guid requestId, Guid approverPrincipalId, bool approverIsAdmin, string? reason, CancellationToken cancellationToken)
    {
        var request = await LoadAsync(tenantId, requestId, cancellationToken);
        if (request is null)
        {
            return null;
        }

        try
        {
            request.Approve(approverPrincipalId, approverIsAdmin, reason, timeProvider.GetUtcNow());
        }
        catch (DomainRuleException ex) when (approverPrincipalId == request.RequestedByPrincipalId)
        {
            await RecordDeniedAsync(tenantId, requestId, approverPrincipalId, "supplier.change_request.approve", ex.Message, cancellationToken);
            throw;
        }

        await audit.RecordAsync(AuditCategories.Mutation(
            tenantId, approverPrincipalId, null, "SupplierChangeRequest", requestId.ToString(),
            "supplier.change_request.approved", request.Type.ToString()), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    public async Task<SupplierChangeRequest?> RejectAsync(
        Guid tenantId, Guid requestId, Guid approverPrincipalId, bool approverIsAdmin, string reason, CancellationToken cancellationToken)
    {
        var request = await LoadAsync(tenantId, requestId, cancellationToken);
        if (request is null)
        {
            return null;
        }

        try
        {
            request.Reject(approverPrincipalId, approverIsAdmin, reason, timeProvider.GetUtcNow());
        }
        catch (DomainRuleException ex) when (approverPrincipalId == request.RequestedByPrincipalId)
        {
            await RecordDeniedAsync(tenantId, requestId, approverPrincipalId, "supplier.change_request.reject", ex.Message, cancellationToken);
            throw;
        }

        await audit.RecordAsync(AuditCategories.Mutation(
            tenantId, approverPrincipalId, null, "SupplierChangeRequest", requestId.ToString(),
            "supplier.change_request.rejected", request.Type.ToString()), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    // Record a high-risk denied attempt (mt6_2) — e.g. a requester trying to decide their own request.
    private async Task RecordDeniedAsync(Guid tenantId, Guid requestId, Guid actorId, string action, string reason, CancellationToken ct)
    {
        await audit.RecordAsync(AuditCategories.DeniedAttempt(
            tenantId, actorId, null, "SupplierChangeRequest", requestId.ToString(), action, reason), ct);
        await db.SaveChangesAsync(ct);
    }

    private Task<SupplierChangeRequest?> LoadAsync(Guid tenantId, Guid requestId, CancellationToken cancellationToken) =>
        db.SupplierChangeRequests.SingleOrDefaultAsync(r => r.Id == requestId && r.TenantId == tenantId, cancellationToken);
}
