namespace ThreeCommerce.Identity.Domain.Tenancy;

/// <summary>
/// A non-human principal used for automation (ADR-0026). Credentials are hash-only: the secret
/// is shown once at creation and stored only as a hash, like sessions/email tokens. Service
/// accounts are narrowly scoped (explicit permissions, never a broad mirror), revocable,
/// rotatable, and may never approve maker-checker changes (ADR-0025).
/// </summary>
public class ServiceAccount
{
    public Guid Id { get; init; }

    /// <summary>The ServiceAccount-typed principal this credential authenticates as.</summary>
    public Guid PrincipalId { get; init; }

    /// <summary>Null = platform-level (MasterGlobal) account; otherwise tenant-scoped.</summary>
    public Guid? TenantId { get; init; }

    public required string Name { get; set; }

    /// <summary>Public client identifier presented at client-credentials login.</summary>
    public required string ClientId { get; init; }

    /// <summary>SHA-256 (or stronger) of the client secret. The secret itself is never stored.</summary>
    public required string SecretHash { get; set; }

    public ServiceAccountStatus Status { get; set; } = ServiceAccountStatus.Active;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? RotatedAt { get; set; }

    public bool IsActive => Status == ServiceAccountStatus.Active;
}

public enum ServiceAccountStatus
{
    Active = 1,
    Revoked = 2,
}
