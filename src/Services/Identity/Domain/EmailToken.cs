namespace ThreeCommerce.Identity.Domain;

public enum EmailTokenPurpose
{
    VerifyEmail = 1,
    ResetPassword = 2,
}

/// <summary>Single-use, expiring token sent by email. Stored hashed, like sessions.</summary>
public class EmailToken
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public required string TokenHash { get; init; }
    public EmailTokenPurpose Purpose { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? UsedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && ExpiresAt > now;
}
