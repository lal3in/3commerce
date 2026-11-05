using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
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

    /// <summary>
    /// The principal behind a tenant user (the internal-claims <c>sub</c>), or null when the user does not exist in
    /// that tenant. Users is a FORCE-RLS table, so the lookup runs in the caller's tenant scope (ADR-0024) — a user
    /// id from another tenant resolves to nothing.
    /// </summary>
    public async Task<Guid?> ResolvePrincipalForUserAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        await db.RunInTenantScopeAsync(TenantContext.ForTenant(tenantId), () => db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.TenantId == tenantId)
            .Select(u => u.PrincipalId)
            .SingleOrDefaultAsync(cancellationToken), cancellationToken);

    /// <summary>The effective authorization context of a tenant user (see <see cref="ResolvePrincipalForUserAsync"/>).</summary>
    public async Task<AuthorizationContext?> ResolveUserContextAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        var principalId = await ResolvePrincipalForUserAsync(userId, tenantId, cancellationToken);
        return principalId is { } id ? await ResolveContextAsync(id, tenantId, cancellationToken) : null;
    }

    public async Task<PolicyDecisionResponse?> DecideAsync(PolicyDecisionRequest request, CancellationToken cancellationToken)
    {
        // A service PEP knows only the user id (sub); resolve its principal inside the claimed tenant. A user-keyed
        // request without a tenant resolves nothing (fail closed).
        AuthorizationContext? context;
        if (request.PrincipalId == Guid.Empty && request.UserId is { } userId)
        {
            context = request.TenantId is { } tenantId
                ? await ResolveUserContextAsync(userId, tenantId, cancellationToken)
                : null;
        }
        else
        {
            context = await ResolveContextAsync(request.PrincipalId, request.TenantId, cancellationToken);
        }

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
    IReadOnlyList<FieldPolicy> Fields,
    Guid? UserId = null);

public sealed record PolicyDecisionResponse(
    Guid DecisionId,
    bool IsPlatformAdmin,
    IReadOnlyList<ActionDecision> Actions,
    IReadOnlyList<FieldDecision> Fields);
