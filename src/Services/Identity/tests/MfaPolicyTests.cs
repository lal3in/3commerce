using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.Identity.Tests;

public class MfaPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_tenant_may_strengthen_the_platform_minimum()
    {
        var policy = new MfaPolicy(MfaRequirement.Optional, MfaRequirement.RequiredForAll);
        Assert.Equal(MfaRequirement.RequiredForAll, policy.Effective);
    }

    [Fact]
    public void A_tenant_cannot_weaken_below_the_platform_minimum()
    {
        var policy = new MfaPolicy(MfaRequirement.RequiredForPrivileged, MfaRequirement.Disabled);
        Assert.Equal(MfaRequirement.RequiredForPrivileged, policy.Effective);
    }

    [Theory]
    [InlineData(MfaRequirement.Disabled, true, false)]
    [InlineData(MfaRequirement.Optional, true, false)]
    [InlineData(MfaRequirement.RequiredForPrivileged, true, true)]
    [InlineData(MfaRequirement.RequiredForPrivileged, false, false)]
    [InlineData(MfaRequirement.RequiredForAll, false, true)]
    public void RequiresMfa_depends_on_effective_level_and_privilege(MfaRequirement effective, bool isPrivileged, bool expected)
    {
        var policy = new MfaPolicy(effective, MfaRequirement.Disabled);
        Assert.Equal(expected, policy.RequiresMfa(isPrivileged));
    }

    [Fact]
    public void Disabled_everywhere_means_no_mfa_the_initial_toggle_state()
    {
        var policy = new MfaPolicy(MfaRequirement.Disabled, MfaRequirement.Disabled);
        Assert.Equal(MfaRequirement.Disabled, policy.Effective);
        Assert.False(policy.RequiresMfa(isPrivileged: true));
    }

    [Fact]
    public void Step_up_is_satisfied_only_by_a_recent_strong_auth()
    {
        Assert.True(StepUp.IsSatisfied(Now.AddMinutes(-3), Now));            // within 5 min
        Assert.False(StepUp.IsSatisfied(Now.AddMinutes(-10), Now));          // stale
        Assert.False(StepUp.IsSatisfied(null, Now));                         // never stepped up
        Assert.False(StepUp.IsSatisfied(Now.AddMinutes(1), Now));            // future (clock skew/forgery)
        Assert.True(StepUp.IsSatisfied(Now.AddMinutes(-10), Now, TimeSpan.FromMinutes(15)));
    }
}
