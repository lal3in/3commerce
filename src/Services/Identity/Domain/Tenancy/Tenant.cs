namespace ThreeCommerce.Identity.Domain.Tenancy;

/// <summary>
/// A Tenant is one legal operating business (ADR-0023). Tenant-owned rows across the
/// platform carry this Id; isolation is enforced by application checks plus PostgreSQL RLS
/// (ADR-0024). One Tenant has many storefronts, staff, suppliers, and customers.
/// </summary>
public class Tenant
{
    public Guid Id { get; init; }

    /// <summary>Human display name of the business.</summary>
    public required string Name { get; set; }

    /// <summary>Stable url/identifier slug, unique across the platform.</summary>
    public required string Slug { get; init; }

    public TenantStatus Status { get; set; } = TenantStatus.Draft;

    /// <summary>Home region for region-aware operation (one physical region in v1, ADR-0023).</summary>
    public required string HomeRegion { get; set; }

    /// <summary>
    /// Forward reference to the owning legal Entity (Phase 2 Entity service). Nullable until
    /// the Entity service exists; the Entity service owns the legal data, not Identity.
    /// </summary>
    public Guid? OwnerLegalEntityRef { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Tenant MFA policy (mt6_10): combined with the platform minimum via <see cref="MfaPolicy"/>,
    /// so a tenant can only strengthen, never weaken, the platform floor.
    /// </summary>
    public MfaRequirement MfaPolicy { get; set; } = MfaRequirement.Disabled;

    public bool IsActive => Status == TenantStatus.Active;
}

public enum TenantStatus
{
    Draft = 1,
    Active = 2,
    Suspended = 3,
    Archived = 4,
}
