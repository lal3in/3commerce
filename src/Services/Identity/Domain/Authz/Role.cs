namespace ThreeCommerce.Identity.Domain.Authz;

/// <summary>
/// An admin-defined role (ADR-0025): roles are DATA mapping to any subset of code-defined
/// permissions. Each tenant is seeded with a default catalog of roles (Admin, Ops, Finance,
/// Support, Merchandiser, + built-in Customer) that operators can edit (add/remove permissions)
/// or clone into new roles. Built-in roles are protected from deletion.
/// </summary>
public class Role
{
    public Guid Id { get; init; }

    /// <summary>Null = system/template role (e.g. the built-in customer role); otherwise tenant-scoped.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Slug, unique within (TenantId, Key).</summary>
    public required string Key { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Seeded/built-in roles cannot be deleted (but their permissions may be edited unless system).</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>Platform-managed role whose permission set is not operator-editable.</summary>
    public bool IsSystem { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public List<RolePermission> Permissions { get; init; } = [];

    /// <summary>
    /// Produce a new editable role for a tenant that starts with this role's permission set
    /// (clone-as-template, ADR-0025). The clone is never built-in or system.
    /// </summary>
    public Role CloneTo(Guid tenantId, string key, string name, DateTimeOffset now)
    {
        var clone = new Role
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Key = key,
            Name = name,
            Description = Description,
            IsBuiltIn = false,
            IsSystem = false,
            CreatedAt = now,
        };
        clone.Permissions.AddRange(Permissions.Select(p => new RolePermission
        {
            RoleId = clone.Id,
            PermissionKey = p.PermissionKey,
        }));
        return clone;
    }
}
