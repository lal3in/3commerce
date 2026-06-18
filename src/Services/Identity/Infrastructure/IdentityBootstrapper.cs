using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Domain.Authz;
using ThreeCommerce.Identity.Domain.Tenancy;

namespace ThreeCommerce.Identity.Infrastructure;

/// <summary>
/// Seeds the code-defined RBAC registry and the single default tenant needed to keep
/// existing single-storefront flows working while the multi-tenant foundation is built.
/// </summary>
public sealed class IdentityBootstrapper(IdentityDbContext db, TimeProvider time)
{
    public const string DefaultTenantSlug = "default";
    public static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public async Task<Tenant> EnsureDefaultTenantAsync(CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Slug == DefaultTenantSlug, cancellationToken);
        if (tenant is not null)
        {
            return tenant;
        }

        tenant = new Tenant
        {
            Id = DefaultTenantId,
            Name = "Default Tenant",
            Slug = DefaultTenantSlug,
            Status = TenantStatus.Active,
            HomeRegion = "AU",
            CreatedAt = time.GetUtcNow(),
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
        return tenant;
    }

    public async Task SeedPermissionRegistryAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        foreach (var permission in PermissionRegistry.Permissions)
        {
            var existing = await db.Permissions.FindAsync([permission.Key], cancellationToken);
            if (existing is null)
            {
                db.Permissions.Add(new Permission
                {
                    Key = permission.Key,
                    Description = permission.Description,
                    RiskLevel = permission.Risk,
                });
            }
            else
            {
                existing.Description = permission.Description;
                existing.RiskLevel = permission.Risk;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var template in PermissionRegistry.DefaultRoles)
        {
            var role = await db.Roles
                .Include(r => r.Permissions)
                .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Key == template.Key, cancellationToken);
            if (role is null)
            {
                role = new Role
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    Key = template.Key,
                    Name = template.Name,
                    Description = template.Description,
                    IsBuiltIn = template.IsBuiltIn,
                    CreatedAt = time.GetUtcNow(),
                };
                db.Roles.Add(role);
            }
            else
            {
                role.Name = template.Name;
                role.Description = template.Description;
            }

            role.Permissions.Clear();
            role.Permissions.AddRange(template.PermissionKeys.Select(key => new RolePermission
            {
                RoleId = role.Id,
                PermissionKey = key,
            }));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Principal> EnsureHumanPrincipalForUserAsync(User user, CancellationToken cancellationToken)
    {
        if (user.PrincipalId is { } existingId)
        {
            return await db.Principals.SingleAsync(p => p.Id == existingId, cancellationToken);
        }

        var principal = new Principal
        {
            Id = Guid.CreateVersion7(),
            Type = PrincipalType.Human,
            DisplayName = user.Email,
            IsPlatformAdmin = user.Role == Roles.Admin,
            CreatedAt = time.GetUtcNow(),
        };
        db.Principals.Add(principal);
        user.PrincipalId = principal.Id;
        return principal;
    }

    public async Task<TenantMembership> EnsureMembershipAsync(
        Guid tenantId,
        Principal principal,
        MembershipKind kind,
        bool isTenantOwner,
        CancellationToken cancellationToken)
    {
        var membership = await db.TenantMemberships
            .SingleOrDefaultAsync(m => m.TenantId == tenantId && m.PrincipalId == principal.Id, cancellationToken);
        if (membership is not null)
        {
            if (isTenantOwner && !membership.IsTenantOwner)
            {
                membership.IsTenantOwner = true;
            }

            return membership;
        }

        membership = new TenantMembership
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            PrincipalId = principal.Id,
            Kind = kind,
            IsTenantOwner = isTenantOwner,
            CreatedAt = time.GetUtcNow(),
        };
        db.TenantMemberships.Add(membership);
        return membership;
    }

    public async Task AssignRoleAsync(TenantMembership membership, string roleKey, CancellationToken cancellationToken)
    {
        var role = await db.Roles.SingleAsync(r => r.TenantId == membership.TenantId && r.Key == roleKey, cancellationToken);
        var assigned = await db.MembershipRoles.AnyAsync(
            mr => mr.TenantMembershipId == membership.Id && mr.RoleId == role.Id,
            cancellationToken);
        if (!assigned)
        {
            db.MembershipRoles.Add(new MembershipRole { TenantMembershipId = membership.Id, RoleId = role.Id });
        }
    }
}
