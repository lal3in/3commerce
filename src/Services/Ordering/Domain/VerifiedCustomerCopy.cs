namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Read copy of a verified account email (fed by Identity's EmailVerified). FR-7 guest-order
/// attachment must work in both directions: EmailVerified sweeps orders that already exist,
/// and this copy lets orders that materialize LATER (payment settles after the user verified)
/// attach at creation time. The security invariant holds either way — a row only exists once
/// the email is verified, so guest orders never attach to an unverified address.
/// </summary>
public class VerifiedCustomerCopy
{
    /// <summary>Normalized (lower-case, trimmed) email — the match key.</summary>
    public required string Email { get; init; }

    public Guid UserId { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
}
