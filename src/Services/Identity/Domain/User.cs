namespace ThreeCommerce.Identity.Domain;

public class User
{
    public Guid Id { get; init; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public bool EmailVerified { get; set; }
    public string Role { get; set; } = Roles.Customer;
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<Address> Addresses { get; init; } = [];
}

public static class Roles
{
    public const string Customer = "customer";
    public const string Admin = "admin";
}
