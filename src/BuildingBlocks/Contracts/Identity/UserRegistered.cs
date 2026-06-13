namespace ThreeCommerce.BuildingBlocks.Contracts.Identity;

/// <summary>
/// Carries the raw verification token so Notifications can build the email link.
/// Acceptable on the private broker for v1: the token is single-use and short-lived.
/// </summary>
public record UserRegistered(Guid UserId, string Email, string VerificationToken);
