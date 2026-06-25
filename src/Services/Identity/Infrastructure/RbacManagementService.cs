using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Identity.Domain.Authz;
using ThreeCommerce.Identity.Domain.Tenancy;

namespace ThreeCommerce.Identity.Infrastructure;

/// <summary>
/// Mutates dynamic RBAC data and invalidates affected principals' claim versions so gateway
/// session introspection re-evaluates permissions promptly after role/membership changes.
/// </summary>
public sealed class RbacManagementService(IdentityDbContext db)
{
    public async Task SetRolePermissionsAsync(Guid roleId, IReadOnlyList<string> permissionKeys, CancellationToken cancellationToken)
    {
        var role = await db.Roles.Include(r => r.Permissions)
            .SingleAsync(r => r.Id == roleId, cancellationToken);

        RbacRules.SetPermissions(role, permissionKeys);
        await InvalidatePrincipalsForRoleAsync(roleId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AssignRoleAsync(Guid membershipId, Guid roleId, CancellationToken cancellationToken)
    {
        var exists = await db.MembershipRoles.AnyAsync(
            mr => mr.TenantMembershipId == membershipId && mr.RoleId == roleId,
            cancellationToken);
        if (exists)
        {
            return;
        }

        db.MembershipRoles.Add(new MembershipRole { TenantMembershipId = membershipId, RoleId = roleId });
        await InvalidatePrincipalForMembershipAsync(membershipId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveRoleAsync(Guid membershipId, Guid roleId, CancellationToken cancellationToken)
    {
        var existing = await db.MembershipRoles.SingleOrDefaultAsync(
            mr => mr.TenantMembershipId == membershipId && mr.RoleId == roleId,
            cancellationToken);
        if (existing is null)
        {
            return;
        }

        db.MembershipRoles.Remove(existing);
        await InvalidatePrincipalForMembershipAsync(membershipId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task InvalidatePrincipalsForRoleAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var principalIds = await db.MembershipRoles
            .Where(mr => mr.RoleId == roleId)
            .Join(db.TenantMemberships, mr => mr.TenantMembershipId, m => m.Id, (_, m) => m.PrincipalId)
            .Distinct()
            .ToListAsync(cancellationToken);

        await InvalidatePrincipalsAsync(principalIds, cancellationToken);
    }

    private async Task InvalidatePrincipalForMembershipAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var principalId = await db.TenantMemberships
            .Where(m => m.Id == membershipId)
            .Select(m => m.PrincipalId)
            .SingleAsync(cancellationToken);

        await InvalidatePrincipalsAsync([principalId], cancellationToken);
    }

    private async Task InvalidatePrincipalsAsync(IEnumerable<Guid> principalIds, CancellationToken cancellationToken)
    {
        var ids = principalIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var principals = await db.Principals.Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        foreach (var principal in principals)
        {
            principal.ClaimsVersion++;
        }
    }
}
