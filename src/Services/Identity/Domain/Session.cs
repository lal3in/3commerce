namespace ThreeCommerce.Identity.Domain;

/// <summary>
/// Server-side session row. Only the SHA-256 of the opaque token is stored —
/// a database leak must not equal session theft (ADR-0012).
/// </summary>
public class Session
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public required string TokenHash { get; init; }
    /// <summary>Principal ClaimsVersion at issuance; mismatch means role/permission changes invalidated it.</summary>
    public int ClaimsVersion { get; init; } = 1;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// True between password success and MFA challenge success (mt6_10). A pending session
    /// introspects as unauthorized — the gateway mints no claims — so the ONLY thing it can do
    /// is complete the challenge (or log out).
    /// </summary>
    public bool MfaPending { get; set; }

    /// <summary>Last strong (second-factor) verification — the StepUp freshness anchor and `auth_time` claim.</summary>
    public DateTimeOffset? StrongAuthAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
