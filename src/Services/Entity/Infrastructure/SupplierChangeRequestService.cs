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
        Guid tenantId, Guid requestId, Guid approverPrincipalId, string? reason, CancellationToken cancellationToken)
    {
        var request = await LoadAsync(tenantId, requestId, cancellationToken);
        if (request is null)
        {
            return null;
        }

        request.Approve(approverPrincipalId, reason, timeProvider.GetUtcNow());
        await audit.RecordAsync(new AuditDraft(
            tenantId, "supplier.change_request.approved", "SupplierChangeRequest", requestId.ToString(),
            AuditOutcome.Success, approverPrincipalId, Summary: request.Type.ToString()), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    public async Task<SupplierChangeRequest?> RejectAsync(
        Guid tenantId, Guid requestId, Guid approverPrincipalId, string reason, CancellationToken cancellationToken)
    {
        var request = await LoadAsync(tenantId, requestId, cancellationToken);
        if (request is null)
        {
            return null;
        }

        request.Reject(approverPrincipalId, reason, timeProvider.GetUtcNow());
        await audit.RecordAsync(new AuditDraft(
            tenantId, "supplier.change_request.rejected", "SupplierChangeRequest", requestId.ToString(),
            AuditOutcome.Success, approverPrincipalId, Summary: request.Type.ToString()), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    private Task<SupplierChangeRequest?> LoadAsync(Guid tenantId, Guid requestId, CancellationToken cancellationToken) =>
        db.SupplierChangeRequests.SingleOrDefaultAsync(r => r.Id == requestId && r.TenantId == tenantId, cancellationToken);
}
