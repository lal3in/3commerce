using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Entity;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Infrastructure;

/// <summary>
/// Supplier change-request lifecycle (mt2_7): the portal raises a request; a tenant admin
/// approves or rejects it with maker-checker (the deciding principal differs from the
/// requester, ADR-0025). Applying an approved change stays with the owning service.
/// Maker-checker decisions are written to the local audit log (mt6_1).
/// </summary>
public sealed class SupplierChangeRequestService(EntityDbContext db, AuditRecorder audit, TimeProvider timeProvider, IPublishEndpoint publish)
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

    /// <summary>The change requests a single supplier has raised — the portal "My details" view of its own history.</summary>
    public Task<List<SupplierChangeRequest>> ListForEntityAsync(Guid tenantId, Guid entityId, SupplierChangeRequestStatus? status, CancellationToken cancellationToken) =>
        db.SupplierChangeRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.EntityId == entityId && (status == null || r.Status == status))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <param name="approverRole">
    /// The deciding principal's <c>role</c> claim (mt6_1) — recorded on the audit entry so a
    /// maker-checker decision shows WHO, in WHICH role, decided. Null only when unauthenticated.
    /// </param>
    public async Task<SupplierChangeRequest?> ApproveAsync(
        Guid tenantId, Guid requestId, Guid approverPrincipalId, string? approverRole, string? reason, CancellationToken cancellationToken)
    {
        var request = await LoadAsync(tenantId, requestId, cancellationToken);
        if (request is null)
        {
            return null;
        }

        try
        {
            request.Approve(approverPrincipalId, reason, timeProvider.GetUtcNow());
        }
        catch (DomainRuleException ex) when (approverPrincipalId == request.RequestedByPrincipalId)
        {
            await RecordDeniedAsync(tenantId, requestId, approverPrincipalId, approverRole, "supplier.change_request.approve", ex.Message, cancellationToken);
            throw;
        }

        // Applying an approved change is the owning service's job (ADR-0025). For an entity-details
        // request the proposed values live in Detail as JSON; apply them to the supplier's record so
        // approval actually updates the details, then persist request + entity in one transaction.
        await ApplyApprovedChangeAsync(request, cancellationToken);

        await audit.RecordAsync(AuditCategories.Mutation(
            tenantId, approverPrincipalId, approverRole, "SupplierChangeRequest", requestId.ToString(),
            "supplier.change_request.approved", request.Type.ToString()), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    /// <param name="approverRole">The deciding principal's <c>role</c> claim — recorded on the audit entry (mt6_1).</param>
    public async Task<SupplierChangeRequest?> RejectAsync(
        Guid tenantId, Guid requestId, Guid approverPrincipalId, string? approverRole, string reason, CancellationToken cancellationToken)
    {
        var request = await LoadAsync(tenantId, requestId, cancellationToken);
        if (request is null)
        {
            return null;
        }

        try
        {
            request.Reject(approverPrincipalId, reason, timeProvider.GetUtcNow());
        }
        catch (DomainRuleException ex) when (approverPrincipalId == request.RequestedByPrincipalId)
        {
            await RecordDeniedAsync(tenantId, requestId, approverPrincipalId, approverRole, "supplier.change_request.reject", ex.Message, cancellationToken);
            throw;
        }

        await audit.RecordAsync(AuditCategories.Mutation(
            tenantId, approverPrincipalId, approverRole, "SupplierChangeRequest", requestId.ToString(),
            "supplier.change_request.rejected", request.Type.ToString()), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    // Record a high-risk denied attempt (mt6_2) — e.g. a requester trying to decide their own request.
    private async Task RecordDeniedAsync(Guid tenantId, Guid requestId, Guid actorId, string? actorRole, string action, string reason, CancellationToken ct)
    {
        await audit.RecordAsync(AuditCategories.DeniedAttempt(
            tenantId, actorId, actorRole, "SupplierChangeRequest", requestId.ToString(), action, reason), ct);
        await db.SaveChangesAsync(ct);
    }

    private Task<SupplierChangeRequest?> LoadAsync(Guid tenantId, Guid requestId, CancellationToken cancellationToken) =>
        db.SupplierChangeRequests.SingleOrDefaultAsync(r => r.Id == requestId && r.TenantId == tenantId, cancellationToken);

    // EntityDetails and WarehouseAddress carry a machine-applicable payload; the other types (user access,
    // contact, bank) are provisioned by their owning surface and remain intent-only records.
    private async Task ApplyApprovedChangeAsync(SupplierChangeRequest request, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case SupplierChangeRequestType.EntityDetails:
                await ApplyEntityDetailsChangeAsync(request, cancellationToken);
                break;
            case SupplierChangeRequestType.WarehouseAddress:
                await ApplyWarehouseAddressChangeAsync(request, cancellationToken);
                break;
        }
    }

    private async Task ApplyEntityDetailsChangeAsync(SupplierChangeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Detail))
        {
            return;
        }

        EntityDetailsChange? change;
        try
        {
            change = JsonSerializer.Deserialize<EntityDetailsChange>(request.Detail, JsonOptions);
        }
        catch (JsonException)
        {
            // Detail is free text (or a legacy shape), not an applicable payload — approve as intent only.
            return;
        }

        if (change is null || string.IsNullOrWhiteSpace(change.LegalName))
        {
            return;
        }

        var entity = await db.Entities.SingleOrDefaultAsync(e => e.Id == request.EntityId, cancellationToken);
        entity?.UpdateNames(change.LegalName, change.TradingName, timeProvider.GetUtcNow());
    }

    // Applying an approved warehouse-address change supersedes the current Warehouse-purpose address with
    // the proposed one and republishes SupplierWarehouseChanged so Ordering's read model tracks it (the
    // same event the direct pre-approval edit publishes). Published before the caller's SaveChanges so the
    // outbox row commits in the same transaction (repo outbox convention).
    private async Task ApplyWarehouseAddressChangeAsync(SupplierChangeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Detail))
        {
            return;
        }

        WarehouseAddressChange? change;
        try
        {
            change = JsonSerializer.Deserialize<WarehouseAddressChange>(request.Detail, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (change is null || string.IsNullOrWhiteSpace(change.Line1) || string.IsNullOrWhiteSpace(change.City)
            || string.IsNullOrWhiteSpace(change.Postcode) || string.IsNullOrWhiteSpace(change.CountryCode))
        {
            return;
        }

        var entity = await db.Entities.Include(e => e.Addresses).SingleOrDefaultAsync(e => e.Id == request.EntityId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var address = entity.AddAddress(
            EntityAddressPurpose.Warehouse, change.Line1, change.Line2, change.City, change.Region, change.Postcode, change.CountryCode, now);
        db.Entry(address).State = EntityState.Added;
        await publish.Publish(new SupplierWarehouseChanged(
            entity.TenantId, entity.Id, entity.DisplayName, address.Line1, address.Line2, address.City, address.Region, address.Postcode, address.CountryCode),
            cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The proposed legal/trading name carried in an <see cref="SupplierChangeRequestType.EntityDetails"/> request's Detail.</summary>
    public sealed record EntityDetailsChange(string LegalName, string? TradingName);

    /// <summary>The proposed warehouse address carried in a <see cref="SupplierChangeRequestType.WarehouseAddress"/> request's Detail.</summary>
    public sealed record WarehouseAddressChange(string Line1, string? Line2, string City, string? Region, string Postcode, string CountryCode);
}
