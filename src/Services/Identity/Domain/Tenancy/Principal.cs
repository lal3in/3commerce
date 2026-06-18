namespace ThreeCommerce.Identity.Domain.Tenancy;

/// <summary>
/// A Principal is one authenticatable identity — human or machine (ADR-0023). A principal may
/// hold memberships in several tenants (staff in one, customer in another). Authentication
/// resolves a principal; authorization always happens within a selected tenant scope.
///
/// A Human principal's login credentials live on <see cref="User"/> (linked by PrincipalId);
/// a ServiceAccount principal's credentials live on <see cref="ServiceAccount"/> (ADR-0026).
/// </summary>
public class Principal
{
    public Guid Id { get; init; }

    public PrincipalType Type { get; init; }

    public string? DisplayName { get; set; }

    /// <summary>
    /// MasterGlobal: a platform operator with cross-tenant scope (ADR-0023). The "at least one
    /// active MasterGlobal" invariant counts active human principals with this flag set.
    /// Bypass is rare, explicit, and audited (ADR-0024/0025).
    /// </summary>
    public bool IsPlatformAdmin { get; set; }

    public PrincipalStatus Status { get; set; } = PrincipalStatus.Active;

    public DateTimeOffset CreatedAt { get; init; }

    public bool IsActive => Status == PrincipalStatus.Active;
}

public enum PrincipalType
{
    Human = 1,
    ServiceAccount = 2,
}

public enum PrincipalStatus
{
    Active = 1,
    Disabled = 2,
}
