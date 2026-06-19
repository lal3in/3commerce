namespace ThreeCommerce.Identity.Domain.Authz;

/// <summary>
/// Assigns a <see cref="Role"/> to a staff <see cref="Tenancy.TenantMembership"/>. A staff
/// member's effective permissions are the union of their assigned roles' permissions, resolved
/// by the PDP and carried in internal claims (ADR-0025).
/// </summary>
public class MembershipRole
{
    public Guid TenantMembershipId { get; init; }

    public Guid RoleId { get; init; }
}
