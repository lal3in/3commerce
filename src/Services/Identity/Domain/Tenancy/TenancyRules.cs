namespace ThreeCommerce.Identity.Domain.Tenancy;

/// <summary>
/// Domain guards for tenancy invariants (ADR-0023): at least one active MasterGlobal on the
/// platform, and at least one active TenantOwner per tenant, at all times.
/// </summary>
public static class TenancyRules
{
    /// <summary>
    /// Guard a change (deactivate/demote/delete) to a tenant owner. Pass the number of active
    /// owners that would remain AFTER the change; throws if it would drop below one.
    /// </summary>
    public static void EnsureTenantOwnerRemains(int activeOwnersAfterChange)
    {
        if (activeOwnersAfterChange < 1)
        {
            throw new DomainRuleException("A tenant must always have at least one active TenantOwner.");
        }
    }

    /// <summary>
    /// Guard a change to a MasterGlobal operator. Pass the number of active MasterGlobals that
    /// would remain AFTER the change; throws if it would drop below one.
    /// </summary>
    public static void EnsureMasterGlobalRemains(int activeMasterGlobalsAfterChange)
    {
        if (activeMasterGlobalsAfterChange < 1)
        {
            throw new DomainRuleException("The platform must always have at least one active MasterGlobal.");
        }
    }
}
