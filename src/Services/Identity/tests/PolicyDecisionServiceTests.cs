using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Identity.Domain.Authz;
using ThreeCommerce.Identity.Domain.Tenancy;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.Identity.Tests;

public class PolicyDecisionServiceTests
{
    [Fact]
    public async Task Staff_decision_uses_union_of_assigned_role_permissions()
    {
        await using var db = NewDb();
        var bootstrapper = new IdentityBootstrapper(db, TimeProvider.System);
        var tenant = await bootstrapper.EnsureDefaultTenantAsync(default);
        await bootstrapper.SeedPermissionRegistryAsync(tenant.Id, default);

        var principal = new Principal
        {
            Id = Guid.CreateVersion7(),
            Type = PrincipalType.Human,
            DisplayName = "ops@example.test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Principals.Add(principal);
        var membership = await bootstrapper.EnsureMembershipAsync(tenant.Id, principal, MembershipKind.Staff, isTenantOwner: false, default);
        await bootstrapper.AssignRoleAsync(membership, "ops", default);
        await db.SaveChangesAsync();

        var service = new PolicyDecisionService(db);
        var response = await service.DecideAsync(new PolicyDecisionRequest(
            principal.Id,
            tenant.Id,
            ["order.view", "payment.refund"],
            []), default);

        Assert.NotNull(response);
        Assert.True(response.Actions.Single(a => a.PermissionKey == "order.view").Allowed);
        Assert.False(response.Actions.Single(a => a.PermissionKey == "payment.refund").Allowed);
    }

    [Fact]
    public async Task Master_global_decision_allows_high_risk_with_reason_not_approval()
    {
        await using var db = NewDb();
        var principal = new Principal
        {
            Id = Guid.CreateVersion7(),
            Type = PrincipalType.Human,
            DisplayName = "master@example.test",
            IsPlatformAdmin = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Principals.Add(principal);
        await db.SaveChangesAsync();

        var service = new PolicyDecisionService(db);
        var response = await service.DecideAsync(new PolicyDecisionRequest(
            principal.Id,
            null,
            ["payment.refund"],
            []), default);

        var decision = Assert.Single(response!.Actions);
        Assert.True(decision.Allowed);
        Assert.True(decision.RequiresReason);
        Assert.False(decision.RequiresApproval);
    }

    // refund_permission_enforcement (owner decisions 2026-11-04 / 2026-11-05): refunds need payment.refund, and the
    // built-in defaults keep today's behaviour — Administrator (every permission), Finance and Support hold it (Support
    // keeps approving returns and instant refunds); Ops, Merchandiser, Customer and Supplier do not.
    [Fact]
    public void Built_in_roles_granting_refunds_are_admin_finance_and_support()
    {
        var granting = PermissionRegistry.DefaultRoles
            .Where(r => r.PermissionKeys.Contains("payment.refund"))
            .Select(r => r.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["admin", "finance", "support"], granting);
        Assert.Equal(PermissionRiskLevel.High, PermissionRegistry.RiskOf("payment.refund"));
    }

    // Every login re-applies the built-in role templates (SeedPermissionRegistryAsync clears and re-adds each built-in
    // role's permissions), so the default must live in the template: it survives a re-seed, and a tenant whose
    // Support role was seeded before 2026-11-05 (no payment.refund) gains it at the next seed.
    [Fact]
    public async Task Support_refund_default_survives_a_reseed_and_reaches_existing_tenants()
    {
        await using var db = NewDb();
        var bootstrapper = new IdentityBootstrapper(db, TimeProvider.System);
        var tenant = await bootstrapper.EnsureDefaultTenantAsync(default);
        await bootstrapper.SeedPermissionRegistryAsync(tenant.Id, default);

        // A pre-decision Support role: payment.refund absent.
        var support = await db.Roles.Include(r => r.Permissions).SingleAsync(r => r.TenantId == tenant.Id && r.Key == "support");
        support.Permissions.RemoveAll(p => p.PermissionKey == "payment.refund");
        await db.SaveChangesAsync();
        Assert.DoesNotContain(support.Permissions, p => p.PermissionKey == "payment.refund");

        await bootstrapper.SeedPermissionRegistryAsync(tenant.Id, default); // what the next login does
        await bootstrapper.SeedPermissionRegistryAsync(tenant.Id, default); // and the one after

        var keys = await db.RolePermissions.Where(p => p.RoleId == support.Id).Select(p => p.PermissionKey).ToListAsync();
        Assert.Single(keys, k => k == "payment.refund");
        Assert.Contains("rma.manage", keys);
    }

    [Theory]
    [InlineData("finance", true)]
    [InlineData("admin", true)]
    [InlineData("support", true)]
    [InlineData("ops", false)]
    [InlineData("merchandiser", false)]
    public async Task Refund_decision_follows_the_assigned_built_in_role(string roleKey, bool allowed)
    {
        await using var db = NewDb();
        var bootstrapper = new IdentityBootstrapper(db, TimeProvider.System);
        var tenant = await bootstrapper.EnsureDefaultTenantAsync(default);
        await bootstrapper.SeedPermissionRegistryAsync(tenant.Id, default);
        var principal = new Principal
        {
            Id = Guid.CreateVersion7(),
            Type = PrincipalType.Human,
            DisplayName = $"{roleKey}@example.test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Principals.Add(principal);
        var membership = await bootstrapper.EnsureMembershipAsync(tenant.Id, principal, MembershipKind.Staff, isTenantOwner: false, default);
        await bootstrapper.AssignRoleAsync(membership, roleKey, default);
        await db.SaveChangesAsync();

        var response = await new PolicyDecisionService(db).DecideAsync(
            new PolicyDecisionRequest(principal.Id, tenant.Id, ["payment.refund"], []), default);

        Assert.Equal(allowed, Assert.Single(response!.Actions).Allowed);
    }

    [Fact]
    public async Task A_user_keyed_request_without_a_tenant_resolves_nothing()
    {
        // A service PEP sends the caller's sub as UserId; with no tenant claim there is no scope to resolve the
        // user in, so the PDP answers "no principal" (the PEP then denies) instead of guessing.
        await using var db = NewDb();

        var response = await new PolicyDecisionService(db).DecideAsync(
            new PolicyDecisionRequest(Guid.Empty, null, ["payment.refund"], [], UserId: Guid.CreateVersion7()), default);

        Assert.Null(response);
    }

    private static IdentityDbContext NewDb() => new(new DbContextOptionsBuilder<IdentityDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);
}
