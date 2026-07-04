namespace ThreeCommerce.Identity.Domain;

/// <summary>
/// A user's TOTP factor (mt6_10 enforcement half). The secret is stored as entered into the
/// authenticator (it must be recoverable to verify codes — unlike passwords/session tokens it
/// cannot be hashed); at-rest protection is the database-encryption posture, and a leak yields
/// second factors only, never passwords or sessions. Recovery codes ARE hashed (verify-only).
/// </summary>
public class MfaEnrollment
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public required string SecretBase32 { get; set; }

    /// <summary>Set when the user proves possession with a first valid code; only then does login challenge.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>SHA-256 hashes of the one-time recovery codes; a used code is removed.</summary>
    public List<string> RecoveryCodeHashes { get; set; } = [];

    public bool IsConfirmed => ConfirmedAt is not null;
}
