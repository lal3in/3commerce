namespace ThreeCommerce.Identity.Domain;

public class User
{
    public Guid Id { get; init; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public bool EmailVerified { get; set; }

    /// <summary>Default tenant/customer scope for the current auth surface. Future APIs select tenant explicitly.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Human principal linked to this credential row (ADR-0023).</summary>
    public Guid? PrincipalId { get; set; }

    /// <summary>Legacy coarse role kept during the Phase-1 migration; dynamic RBAC becomes authoritative in mt1_7.</summary>
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
