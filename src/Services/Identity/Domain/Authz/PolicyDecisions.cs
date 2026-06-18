namespace ThreeCommerce.Identity.Domain.Authz;

/// <summary>
/// The authorization context for one principal within one selected tenant scope (ADR-0023/0025):
/// the effective set of granted permission keys, plus whether the principal is a MasterGlobal
/// platform operator. The PDP turns this into action/field decisions.
/// </summary>
public sealed class AuthorizationContext(IReadOnlySet<string> grantedPermissions, bool isPlatformAdmin)
{
    public bool IsPlatformAdmin { get; } = isPlatformAdmin;

    public IReadOnlySet<string> GrantedPermissions { get; } = grantedPermissions;

    public bool Has(string permissionKey) => GrantedPermissions.Contains(permissionKey);
}

/// <summary>How a field may be accessed in a given context (ADR-0025).</summary>
public enum FieldAccess
{
    /// <summary>Not viewable — omit from output entirely.</summary>
    Hidden = 0,

    /// <summary>Viewable only as a masked value; the real value needs an explicit reveal.</summary>
    Masked = 1,

    /// <summary>Viewable but not editable.</summary>
    ReadOnly = 2,

    /// <summary>Viewable and editable.</summary>
    Editable = 3,
}

/// <summary>
/// Decision for one requested action. <see cref="RequiresReason"/> marks a MasterGlobal bypass
/// of a high-risk action (must supply a reason + audit); <see cref="RequiresApproval"/> marks a
/// high-risk action by ordinary staff that needs maker-checker approval (ADR-0025).
/// </summary>
public sealed record ActionDecision(
    string PermissionKey,
    bool Allowed,
    PermissionRiskLevel Risk,
    bool RequiresReason,
    bool RequiresApproval);

/// <summary>
/// Declares how a single output/input field is gated: the permission needed to view it, the
/// permission needed to edit it (null = never editable here), and whether it is sensitive
/// (masked, reveal-with-reason) even to those who may view it.
/// </summary>
public sealed record FieldPolicy(
    string Field,
    string? ViewPermission,
    string? EditPermission,
    bool Sensitive);

/// <summary>Decision for one field.</summary>
public sealed record FieldDecision(
    string Field,
    FieldAccess Access,
    bool RequiresRevealReason);
