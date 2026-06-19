namespace ThreeCommerce.Identity.Domain.Authz;

/// <summary>
/// A code-defined permission (ADR-0025). Permissions are NOT operator-editable — they
/// self-register from the <see cref="PermissionRegistry"/> at startup and are persisted so
/// that role mappings can reference them by key. A role may never grant a key absent here.
/// </summary>
public class Permission
{
    /// <summary>Dotted key, e.g. "catalog.product.edit". Primary key.</summary>
    public required string Key { get; init; }

    public required string Description { get; set; }

    public PermissionRiskLevel RiskLevel { get; set; } = PermissionRiskLevel.Low;
}

/// <summary>
/// Drives fail-closed behaviour and approval requirements: high-risk decisions deny when the
/// PDP is unavailable; only low-risk reads may use a short cached decision (ADR-0025).
/// </summary>
public enum PermissionRiskLevel
{
    Low = 1,
    Medium = 2,
    High = 3,
}
