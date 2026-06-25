namespace ThreeCommerce.BuildingBlocks.Infrastructure.Approval;

public sealed class ApprovalRuleException(string message) : Exception(message);

public enum ApprovalStatus { Pending = 1, Approved = 2, Rejected = 3, Expired = 4 }

/// <summary>How urgent the approval is — drives how quickly the task expires (mt6_4).</summary>
public enum ApprovalRisk { Low = 1, Medium = 2, High = 3 }

/// <summary>
/// The principal deciding an approval, with the attributes the rules care about (mt6_4). Sourced from
/// the PDP / internal claims, never from the request body.
/// </summary>
public sealed record ApprovalActor(Guid PrincipalId, bool IsServiceAccount = false, bool IsMasterGlobal = false);

/// <summary>The maker-checker decision rules (mt6_4 GOTCHA), reusable across services.</summary>
public static class ApprovalRules
{
    public static void EnsureCanDecide(ApprovalActor approver, Guid requesterId, bool approve, string? reason)
    {
        if (approver.PrincipalId == Guid.Empty)
        {
            throw new ApprovalRuleException("A deciding principal is required.");
        }

        if (!approve && string.IsNullOrWhiteSpace(reason))
        {
            throw new ApprovalRuleException("Rejecting an approval requires a reason.");
        }

        // MasterGlobal (platform scope) may override maker-checker / service-account rules — but must
        // record a reason, and the override is audited by the caller (ADR-0024 / mt6_2).
        if (approver.IsMasterGlobal)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ApprovalRuleException("A MasterGlobal override requires a reason.");
            }

            return;
        }

        if (approver.IsServiceAccount)
        {
            throw new ApprovalRuleException("Service accounts cannot approve.");
        }

        if (approver.PrincipalId == requesterId)
        {
            throw new ApprovalRuleException("The requester cannot decide their own request (maker-checker).");
        }
    }
}

/// <summary>
/// A generic pending approval (mt6_4): a typed change waiting on a maker-checker decision, with
/// risk-driven expiry. The owning service holds the pending change and applies it once this approves.
/// </summary>
public sealed class ApprovalTask
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string Action { get; init; }
    public required string ResourceType { get; init; }
    public required string ResourceId { get; init; }
    public Guid RequesterId { get; init; }
    public ApprovalRisk Risk { get; init; }
    public ApprovalStatus Status { get; private set; } = ApprovalStatus.Pending;
    public Guid? DecidedById { get; private set; }
    public string? DecisionReason { get; private set; }
    public bool WasOverride { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? DecidedAt { get; private set; }

    public bool IsPending => Status == ApprovalStatus.Pending;

    private ApprovalTask() { }

    public static ApprovalTask Open(
        Guid tenantId, string action, string resourceType, string resourceId, Guid requesterId, ApprovalRisk risk, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            RequesterId = requesterId,
            Risk = risk,
            CreatedAt = now,
            ExpiresAt = now + ExpiryFor(risk),
        };

    public void Decide(ApprovalActor approver, bool approve, string? reason, DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Pending)
        {
            throw new ApprovalRuleException("This approval has already been decided.");
        }

        if (now >= ExpiresAt)
        {
            Status = ApprovalStatus.Expired;
            throw new ApprovalRuleException("This approval has expired.");
        }

        ApprovalRules.EnsureCanDecide(approver, RequesterId, approve, reason);

        Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        DecidedById = approver.PrincipalId;
        DecisionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        WasOverride = approver.IsMasterGlobal;
        DecidedAt = now;
    }

    /// <summary>Sweep a pending task that has passed its deadline (mt6_4; a scheduled job calls this).</summary>
    public bool Expire(DateTimeOffset now)
    {
        if (Status == ApprovalStatus.Pending && now >= ExpiresAt)
        {
            Status = ApprovalStatus.Expired;
            return true;
        }

        return false;
    }

    /// <summary>Higher risk expires sooner — less time for an unreviewed change to linger.</summary>
    public static TimeSpan ExpiryFor(ApprovalRisk risk) => risk switch
    {
        ApprovalRisk.High => TimeSpan.FromDays(1),
        ApprovalRisk.Medium => TimeSpan.FromDays(3),
        _ => TimeSpan.FromDays(7),
    };
}
