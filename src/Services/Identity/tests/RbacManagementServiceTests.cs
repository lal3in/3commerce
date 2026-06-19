using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Identity.Domain.Authz;
using ThreeCommerce.Identity.Domain.Tenancy;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.Identity.Tests;

public class RbacManagementServiceTests
{
    [Fact]
    public async Task Editing_role_permissions_increments_assigned_principals_claim_version()
    {
        await using var db = NewDb();
        var bootstrapper = new IdentityBootstrapper(db, TimeProvider.System);
        var tenant = await bootstrapper.EnsureDefaultTenantAsync(default);
        await bootstrapper.SeedPermissionRegistryAsync(tenant.Id, default);
        var principal = await AddPrincipalWithRoleAsync(db, bootstrapper, tenant.Id, "ops");
        var before = principal.ClaimsVersion;
        var roleId = await db.Roles.Where(r => r.TenantId == tenant.Id && r.Key == "ops").Select(r => r.Id).SingleAsync();

        await new RbacManagementService(db).SetRolePermissionsAsync(roleId, ["order.view"], default);

        Assert.Equal(before + 1, (await db.Principals.FindAsync(principal.Id))!.ClaimsVersion);
    }

    [Fact]
    public async Task Assigning_role_increments_members_principal_claim_version()
    {
        await using var db = NewDb();
        var bootstrapper = new IdentityBootstrapper(db, TimeProvider.System);
        var tenant = await bootstrapper.EnsureDefaultTenantAsync(default);
        await bootstrapper.SeedPermissionRegistryAsync(tenant.Id, default);
        var principal = await AddPrincipalWithRoleAsync(db, bootstrapper, tenant.Id, "support");
        var membershipId = await db.TenantMemberships.Where(m => m.PrincipalId == principal.Id).Select(m => m.Id).SingleAsync();
        var financeRoleId = await db.Roles.Where(r => r.TenantId == tenant.Id && r.Key == "finance").Select(r => r.Id).SingleAsync();

        await new RbacManagementService(db).AssignRoleAsync(membershipId, financeRoleId, default);

        Assert.Equal(2, (await db.Principals.FindAsync(principal.Id))!.ClaimsVersion);
    }

    private static async Task<Principal> AddPrincipalWithRoleAsync(
        IdentityDbContext db,
        IdentityBootstrapper bootstrapper,
        Guid tenantId,
        string roleKey)
    {
        var principal = new Principal
        {
            Id = Guid.CreateVersion7(),
            Type = PrincipalType.Human,
            DisplayName = $"{roleKey}@example.test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Principals.Add(principal);
        var membership = await bootstrapper.EnsureMembershipAsync(tenantId, principal, MembershipKind.Staff, isTenantOwner: false, default);
        await bootstrapper.AssignRoleAsync(membership, roleKey, default);
        await db.SaveChangesAsync();
        return principal;
    }

    private static IdentityDbContext NewDb() => new(new DbContextOptionsBuilder<IdentityDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);
}
