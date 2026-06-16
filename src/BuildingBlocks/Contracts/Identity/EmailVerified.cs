namespace ThreeCommerce.BuildingBlocks.Contracts.Identity;

/// <summary>
/// Published when a user verifies their email. Ordering uses it to attach prior guest
/// orders (placed with that email) to the account — the secure FR-7 mechanism: orders
/// only attach to a *verified* email, never an unverified one.
/// </summary>
public record EmailVerified(Guid UserId, string Email);
