using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Domain.Authz;
using ThreeCommerce.Identity.Domain.Tenancy;

namespace ThreeCommerce.Identity.Tests;

public class RbacAndTenancyTests
{
    [Fact]
    public void Permission_registry_keys_are_unique()
    {
        var keys = PermissionRegistry.AllPermissionKeys;
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Default_roles_only_reference_known_permissions()
    {
        foreach (var role in PermissionRegistry.DefaultRoles)
        {
            // Should not throw: every seeded role maps only to registry permissions.
            RbacRules.EnsureKnownPermissions(role.PermissionKeys);
        }
    }

    [Fact]
    public void Admin_default_role_grants_every_permission()
    {
        var admin = PermissionRegistry.DefaultRoles.Single(r => r.Key == "admin");
        Assert.Equal(
            PermissionRegistry.AllPermissionKeys.OrderBy(k => k),
            admin.PermissionKeys.OrderBy(k => k));
    }

    [Fact]
    public void Customer_is_built_in_with_no_backoffice_permissions()
    {
        var customer = PermissionRegistry.DefaultRoles.Single(r => r.Key == "customer");
        Assert.True(customer.IsBuiltIn);
        Assert.Empty(customer.PermissionKeys);
    }

    [Fact]
    public void SetPermissions_rejects_an_unknown_permission()
    {
        var role = NewTenantRole();
        var ex = Assert.Throws<DomainRuleException>(
            () => RbacRules.SetPermissions(role, ["order.view", "totally.made.up"]));
        Assert.Contains("totally.made.up", ex.Message);
        // All-or-nothing: nothing was applied.
        Assert.Empty(role.Permissions);
    }

    [Fact]
    public void SetPermissions_applies_known_permissions()
    {
        var role = NewTenantRole();
        RbacRules.SetPermissions(role, ["order.view", "order.manage", "order.view"]);
        Assert.Equal(["order.manage", "order.view"], role.Permissions.Select(p => p.PermissionKey).OrderBy(k => k));
    }

    [Fact]
    public void SetPermissions_on_a_system_role_throws()
    {
        var system = new Role { Id = Guid.NewGuid(), Key = "platform", Name = "Platform", IsSystem = true };
        Assert.Throws<DomainRuleException>(() => RbacRules.SetPermissions(system, ["order.view"]));
    }

    [Fact]
    public void Cloning_a_role_copies_permissions_and_is_not_built_in()
    {
        var tenantId = Guid.NewGuid();
        var source = NewTenantRole(tenantId);
        RbacRules.SetPermissions(source, ["order.view", "payment.refund"]);

        var clone = source.CloneTo(tenantId, "order-refunder", "Order Refunder", DateTimeOffset.UtcNow);

        Assert.False(clone.IsBuiltIn);
        Assert.False(clone.IsSystem);
        Assert.NotEqual(source.Id, clone.Id);
        Assert.Equal(
            source.Permissions.Select(p => p.PermissionKey).OrderBy(k => k),
            clone.Permissions.Select(p => p.PermissionKey).OrderBy(k => k));
        Assert.All(clone.Permissions, p => Assert.Equal(clone.Id, p.RoleId));
    }

    [Fact]
    public void Built_in_role_cannot_be_deleted()
    {
        var builtIn = new Role { Id = Guid.NewGuid(), Key = "admin", Name = "Administrator", IsBuiltIn = true };
        Assert.Throws<DomainRuleException>(() => RbacRules.EnsureDeletable(builtIn));
    }

    [Fact]
    public void Custom_role_can_be_deleted()
    {
        RbacRules.EnsureDeletable(NewTenantRole()); // does not throw
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void Last_tenant_owner_is_protected(int ownersAfter, bool shouldThrow)
    {
        if (shouldThrow)
        {
            Assert.Throws<DomainRuleException>(() => TenancyRules.EnsureTenantOwnerRemains(ownersAfter));
        }
        else
        {
            TenancyRules.EnsureTenantOwnerRemains(ownersAfter);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Last_master_global_is_protected(int mastersAfter, bool shouldThrow)
    {
        if (shouldThrow)
        {
            Assert.Throws<DomainRuleException>(() => TenancyRules.EnsureMasterGlobalRemains(mastersAfter));
        }
        else
        {
            TenancyRules.EnsureMasterGlobalRemains(mastersAfter);
        }
    }

    private static Role NewTenantRole(Guid? tenantId = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId ?? Guid.NewGuid(),
        Key = "custom",
        Name = "Custom",
    };
}
