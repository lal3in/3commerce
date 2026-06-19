namespace ThreeCommerce.Identity.Domain.Authz;

/// <summary>Join row: a <see cref="Role"/> grants a code-defined <see cref="Permission"/> (by key).</summary>
public class RolePermission
{
    public Guid RoleId { get; init; }

    public required string PermissionKey { get; init; }
}
