namespace ThreeCommerce.Identity.Domain.Authz;

/// <summary>
/// Domain guards for the dynamic RBAC model (ADR-0025). Roles are data; permissions are
/// code-defined — these rules keep the two consistent and protect built-in roles.
/// </summary>
public static class RbacRules
{
    /// <summary>A role may never reference a permission absent from the registry.</summary>
    public static void EnsureKnownPermissions(IEnumerable<string> permissionKeys)
    {
        foreach (var key in permissionKeys)
        {
            if (!PermissionRegistry.IsKnown(key))
            {
                throw new DomainRuleException($"Unknown permission '{key}'. Permissions are code-defined (PermissionRegistry).");
            }
        }
    }

    /// <summary>Built-in and system roles cannot be deleted.</summary>
    public static void EnsureDeletable(Role role)
    {
        if (role.IsBuiltIn)
        {
            throw new DomainRuleException($"Built-in role '{role.Key}' cannot be deleted.");
        }

        if (role.IsSystem)
        {
            throw new DomainRuleException($"System role '{role.Key}' cannot be deleted.");
        }
    }

    /// <summary>System roles have a platform-managed permission set that operators cannot edit.</summary>
    public static void EnsurePermissionsEditable(Role role)
    {
        if (role.IsSystem)
        {
            throw new DomainRuleException($"System role '{role.Key}' permissions are not editable.");
        }
    }

    /// <summary>
    /// Set a role's permissions, validating every key against the registry first
    /// (all-or-nothing: an unknown key changes nothing).
    /// </summary>
    public static void SetPermissions(Role role, IEnumerable<string> permissionKeys)
    {
        EnsurePermissionsEditable(role);
        var keys = permissionKeys.Distinct(StringComparer.Ordinal).ToArray();
        EnsureKnownPermissions(keys);
        role.Permissions.Clear();
        role.Permissions.AddRange(keys.Select(k => new RolePermission { RoleId = role.Id, PermissionKey = k }));
    }
}
