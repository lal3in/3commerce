namespace ThreeCommerce.Entity.Domain;

/// <summary>What a supplier is asking the tenant to change (ADR-0025 maker-checker territory).</summary>
public enum SupplierChangeRequestType
{
    /// <summary>Add/remove a supplier portal user or change their access.</summary>
    UserAccess = 1,

    /// <summary>Change a supplier contact method.</summary>
    Contact = 2,

    /// <summary>Change supplier bank/payout details (sensitive — Payments owns the actual instrument).</summary>
    BankAccount = 3,
}

public enum SupplierChangeRequestStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
}

/// <summary>
/// A request raised from the supplier portal (mt2_6) that a tenant admin must approve or reject
/// (mt2_7). Approval is maker-checker: the deciding principal must differ from the requester
/// (ADR-0025). The request only records intent — applying an approved change (e.g. provisioning
/// a portal user, or updating a bank instrument in Payments) is the owning service's job.
/// </summary>
public sealed class SupplierChangeRequest
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid EntityId { get; init; }
    public SupplierChangeRequestType Type { get; private set; }
    public string Summary { get; private set; } = string.Empty;

    /// <summary>Optional structured detail; sensitive values (e.g. bank) are masked, never raw.</summary>
    public string? Detail { get; private set; }

    public SupplierChangeRequestStatus Status { get; private set; } = SupplierChangeRequestStatus.Pending;
    public Guid RequestedByPrincipalId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public Guid? DecidedByPrincipalId { get; private set; }
    public string? DecisionReason { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    private SupplierChangeRequest()
    {
    }

    public static SupplierChangeRequest Open(
        Guid tenantId,
        Guid entityId,
        SupplierChangeRequestType type,
        string summary,
        string? detail,
        Guid requestedByPrincipalId,
        DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainRuleException("TenantId is required.");
        }

        if (entityId == Guid.Empty)
        {
            throw new DomainRuleException("EntityId is required.");
        }

        if (requestedByPrincipalId == Guid.Empty)
        {
            throw new DomainRuleException("Requesting principal is required.");
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new DomainRuleException("A change request needs a summary.");
        }

        return new SupplierChangeRequest
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            EntityId = entityId,
            Type = type,
            Summary = summary.Trim(),
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
            RequestedByPrincipalId = requestedByPrincipalId,
            CreatedAt = now,
        };
    }

    public void Approve(Guid approverPrincipalId, string? reason, DateTimeOffset now)
    {
        EnsureDecidable(approverPrincipalId);
        Status = SupplierChangeRequestStatus.Approved;
        DecidedByPrincipalId = approverPrincipalId;
        DecisionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        DecidedAt = now;
    }

    public void Reject(Guid approverPrincipalId, string reason, DateTimeOffset now)
    {
        EnsureDecidable(approverPrincipalId);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainRuleException("Rejecting a change request requires a reason.");
        }

        Status = SupplierChangeRequestStatus.Rejected;
        DecidedByPrincipalId = approverPrincipalId;
        DecisionReason = reason.Trim();
        DecidedAt = now;
    }

    private void EnsureDecidable(Guid approverPrincipalId)
    {
        if (Status != SupplierChangeRequestStatus.Pending)
        {
            throw new DomainRuleException("Only a pending change request can be approved or rejected.");
        }

        if (approverPrincipalId == Guid.Empty)
        {
            throw new DomainRuleException("Deciding principal is required.");
        }

        // Maker-checker (ADR-0025): the requester cannot approve their own request.
        if (approverPrincipalId == RequestedByPrincipalId)
        {
            throw new DomainRuleException("A change request cannot be decided by its requester (maker-checker).");
        }
    }
}
