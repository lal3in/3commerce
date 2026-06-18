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

    private static IdentityDbContext NewDb() => new(new DbContextOptionsBuilder<IdentityDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);
}
