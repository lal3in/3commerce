namespace ThreeCommerce.BuildingBlocks.Contracts.Identity;

public record PasswordResetRequested(Guid UserId, string Email, string ResetToken);
