namespace ThreeCommerce.Identity.Domain;

/// <summary>
/// How strongly MFA is enforced (mt6_10). Ordered weakest→strongest so a tenant policy can only ever
/// strengthen the platform minimum. <see cref="Disabled"/> is the initial state (the feature toggle —
/// MFA can be off to start, but the model supports turning it on; mt6_10 GOTCHA).
/// </summary>
public enum MfaRequirement
{
    Disabled = 0,
    Optional = 1,
    RequiredForPrivileged = 2,
    RequiredForAll = 3,
}

/// <summary>
/// The resolved MFA policy for a tenant (mt6_10): the platform minimum is a floor a tenant may raise but
/// never lower. The <see cref="Effective"/> requirement is the stronger of the two.
/// </summary>
public sealed record MfaPolicy(MfaRequirement PlatformMinimum, MfaRequirement TenantPolicy)
{
    public MfaRequirement Effective => (MfaRequirement)Math.Max((int)PlatformMinimum, (int)TenantPolicy);

    /// <summary>Whether a sign-in must complete MFA, given whether the user holds a privileged role.</summary>
    public bool RequiresMfa(bool isPrivileged) => Effective switch
    {
        MfaRequirement.RequiredForAll => true,
        MfaRequirement.RequiredForPrivileged => isPrivileged,
        _ => false,
    };
}

/// <summary>
/// DI carrier for the configured platform floor (`Mfa:PlatformMinimum`, numeric per the wire rule).
/// Combined with the tenant's policy at every evaluation site via <see cref="MfaPolicy"/>.
/// </summary>
public sealed record MfaPlatformPolicy(MfaRequirement Minimum);

/// <summary>
/// Step-up authentication for high-risk actions (mt6_10): even with a valid session, a sensitive action
/// (approvals, secret reveals, payout changes) requires a recent strong re-auth. The action handler
/// checks the principal's last strong-auth timestamp against a freshness window.
/// </summary>
public static class StepUp
{
    public static readonly TimeSpan DefaultFreshness = TimeSpan.FromMinutes(5);

    public static bool IsSatisfied(DateTimeOffset? lastStrongAuthAt, DateTimeOffset now, TimeSpan? freshness = null)
    {
        if (lastStrongAuthAt is not { } authedAt)
        {
            return false;
        }

        var age = now - authedAt;
        return age >= TimeSpan.Zero && age <= (freshness ?? DefaultFreshness);
    }
}
