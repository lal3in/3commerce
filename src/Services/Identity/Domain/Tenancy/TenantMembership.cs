namespace ThreeCommerce.Identity.Domain.Tenancy;

/// <summary>
/// Links a <see cref="Principal"/> to a <see cref="Tenant"/> with a kind (staff or customer).
/// A principal has at most one membership per tenant. Staff memberships carry role assignments
/// (RBAC, ADR-0025); the <see cref="IsTenantOwner"/> flag backs the "at least one active
/// TenantOwner per tenant" invariant (ADR-0023).
/// </summary>
public class TenantMembership
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid PrincipalId { get; init; }

    public MembershipKind Kind { get; init; }

    /// <summary>A protected owner of the tenant. Staff only. Min one active per tenant.</summary>
    public bool IsTenantOwner { get; set; }

    public MembershipStatus Status { get; set; } = MembershipStatus.Active;

    public DateTimeOffset CreatedAt { get; init; }

    public bool IsActiveOwner => IsTenantOwner && Status == MembershipStatus.Active;
}

public enum MembershipKind
{
    Staff = 1,
    Customer = 2,
}

public enum MembershipStatus
{
    Active = 1,
    Suspended = 2,
}
