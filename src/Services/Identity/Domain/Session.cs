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
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
