using ThreeCommerce.Identity.Domain.Authz;

namespace ThreeCommerce.Identity.Tests;

public class PolicyEngineTests
{
    private static AuthorizationContext Staff(params string[] perms) =>
        new(perms.ToHashSet(StringComparer.Ordinal), isPlatformAdmin: false);

    private static AuthorizationContext MasterGlobal() =>
        new(new HashSet<string>(), isPlatformAdmin: true);

    [Fact]
    public void Granted_action_is_allowed_and_reports_risk()
    {
        var d = PolicyEngine.DecideAction(Staff("order.view"), "order.view");
        Assert.True(d.Allowed);
        Assert.Equal(PermissionRiskLevel.Low, d.Risk);
        Assert.False(d.RequiresApproval);
        Assert.False(d.RequiresReason);
    }

    [Fact]
    public void Ungranted_action_is_denied_for_staff()
    {
        var d = PolicyEngine.DecideAction(Staff("order.view"), "payment.refund");
        Assert.False(d.Allowed);
    }

    [Fact]
    public void Master_global_is_allowed_everything()
    {
        var d = PolicyEngine.DecideAction(MasterGlobal(), "payment.refund");
        Assert.True(d.Allowed);
    }

    [Fact]
    public void High_risk_action_by_staff_requires_approval_not_reason()
    {
        var d = PolicyEngine.DecideAction(Staff("payment.refund"), "payment.refund");
        Assert.True(d.Allowed);
        Assert.Equal(PermissionRiskLevel.High, d.Risk);
        Assert.True(d.RequiresApproval);
        Assert.False(d.RequiresReason);
    }

    [Fact]
    public void High_risk_action_by_master_global_requires_reason_not_approval()
    {
        var d = PolicyEngine.DecideAction(MasterGlobal(), "payment.refund");
        Assert.True(d.Allowed);
        Assert.True(d.RequiresReason);
        Assert.False(d.RequiresApproval);
    }

    [Fact]
    public void Low_risk_action_needs_neither_reason_nor_approval()
    {
        var d = PolicyEngine.DecideAction(Staff("order.view"), "order.view");
        Assert.False(d.RequiresReason);
        Assert.False(d.RequiresApproval);
    }

    [Fact]
    public void Actions_are_decided_in_batch()
    {
        var ctx = Staff("order.view", "order.manage");
        var decisions = PolicyEngine.DecideActions(ctx, ["order.view", "order.manage", "payment.refund"]);
        Assert.Equal(3, decisions.Count);
        Assert.True(decisions[0].Allowed);
        Assert.True(decisions[1].Allowed);
        Assert.False(decisions[2].Allowed);
    }

    [Fact]
    public void Field_without_view_permission_is_hidden()
    {
        var policy = new FieldPolicy("supplierCost", ViewPermission: "supplier.manage", EditPermission: "supplier.manage", Sensitive: false);
        var d = PolicyEngine.DecideField(Staff("order.view"), policy);
        Assert.Equal(FieldAccess.Hidden, d.Access);
    }

    [Fact]
    public void Field_with_view_and_edit_permission_is_editable()
    {
        var policy = new FieldPolicy("title", ViewPermission: "catalog.product.view", EditPermission: "catalog.product.edit", Sensitive: false);
        var d = PolicyEngine.DecideField(Staff("catalog.product.view", "catalog.product.edit"), policy);
        Assert.Equal(FieldAccess.Editable, d.Access);
    }

    [Fact]
    public void Field_viewable_without_edit_permission_is_read_only()
    {
        var policy = new FieldPolicy("title", ViewPermission: "catalog.product.view", EditPermission: "catalog.product.edit", Sensitive: false);
        var d = PolicyEngine.DecideField(Staff("catalog.product.view"), policy);
        Assert.Equal(FieldAccess.ReadOnly, d.Access);
    }

    [Fact]
    public void Public_field_with_no_view_permission_is_visible()
    {
        var policy = new FieldPolicy("name", ViewPermission: null, EditPermission: null, Sensitive: false);
        var d = PolicyEngine.DecideField(Staff(), policy);
        Assert.Equal(FieldAccess.ReadOnly, d.Access);
    }

    [Fact]
    public void Sensitive_field_is_masked_and_reveal_needs_reason_even_for_master_global()
    {
        var policy = new FieldPolicy("bankAccount", ViewPermission: "supplier.manage", EditPermission: null, Sensitive: true);

        var staff = PolicyEngine.DecideField(Staff("supplier.manage"), policy);
        Assert.Equal(FieldAccess.Masked, staff.Access);
        Assert.True(staff.RequiresRevealReason);

        var master = PolicyEngine.DecideField(MasterGlobal(), policy);
        Assert.Equal(FieldAccess.Masked, master.Access);
        Assert.True(master.RequiresRevealReason);
    }
}
