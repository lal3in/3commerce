using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Identity.Domain.Authz;

namespace ThreeCommerce.Identity.Infrastructure;

/// <summary>Identity/Authz PDP adapter: resolves persisted role assignments into pure policy decisions.</summary>
public sealed class PolicyDecisionService(IdentityDbContext db)
{
    public async Task<AuthorizationContext?> ResolveContextAsync(Guid principalId, Guid? tenantId, CancellationToken cancellationToken)
    {
        var principal = await db.Principals.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == principalId, cancellationToken);
        if (principal is null || !principal.IsActive)
        {
            return null;
        }

        if (principal.IsPlatformAdmin)
        {
            return new AuthorizationContext(new HashSet<string>(StringComparer.Ordinal), isPlatformAdmin: true);
        }

        if (tenantId is null)
        {
            return new AuthorizationContext(new HashSet<string>(StringComparer.Ordinal), isPlatformAdmin: false);
        }

        var permissions = await db.TenantMemberships.AsNoTracking()
            .Where(m => m.PrincipalId == principalId && m.TenantId == tenantId && m.Status == Domain.Tenancy.MembershipStatus.Active)
            .Join(db.MembershipRoles.AsNoTracking(), m => m.Id, mr => mr.TenantMembershipId, (_, mr) => mr.RoleId)
            .Join(db.RolePermissions.AsNoTracking(), roleId => roleId, rp => rp.RoleId, (_, rp) => rp.PermissionKey)
            .Distinct()
            .ToListAsync(cancellationToken);

        return new AuthorizationContext(permissions.ToHashSet(StringComparer.Ordinal), isPlatformAdmin: false);
    }

    public async Task<PolicyDecisionResponse?> DecideAsync(PolicyDecisionRequest request, CancellationToken cancellationToken)
    {
        var context = await ResolveContextAsync(request.PrincipalId, request.TenantId, cancellationToken);
        if (context is null)
        {
            return null;
        }

        var actions = PolicyEngine.DecideActions(context, request.Actions).ToArray();
        var fields = PolicyEngine.DecideFields(context, request.Fields).ToArray();
        return new PolicyDecisionResponse(Guid.CreateVersion7(), context.IsPlatformAdmin, actions, fields);
    }
}

public sealed record PolicyDecisionRequest(
    Guid PrincipalId,
    Guid? TenantId,
    IReadOnlyList<string> Actions,
    IReadOnlyList<FieldPolicy> Fields);

public sealed record PolicyDecisionResponse(
    Guid DecisionId,
    bool IsPlatformAdmin,
    IReadOnlyList<ActionDecision> Actions,
    IReadOnlyList<FieldDecision> Fields);
