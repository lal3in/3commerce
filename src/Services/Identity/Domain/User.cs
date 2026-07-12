namespace ThreeCommerce.Identity.Domain;

public class User
{
    public Guid Id { get; init; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }

    // Structured legal name (mem_1) — recurrent-payment/member services need the full name, not a
    // single free-text field. Title/Middle/Preferred are optional; First/Last are the member's
    // required legal name (enforced at the API for customer self-service).
    public string? Title { get; set; }
    public string? FirstName { get; set; }
    public string? MiddleName { get; set; }
    public string? LastName { get; set; }
    public string? PreferredName { get; set; }

    // Contact + compliance fields real subscription services collect.
    public string? Phone { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public bool MarketingConsent { get; set; }

    public bool EmailVerified { get; set; }

    /// <summary>Display name — preferred name if set, else "First Last", else the email local part.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(PreferredName) ? PreferredName!
        : string.Join(" ", new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } fl ? fl
        : Email.Split('@')[0];

    /// <summary>Full legal name for billing/address defaults: "Title First Middle Last".</summary>
    public string FullName =>
        string.Join(" ", new[] { Title, FirstName, MiddleName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));

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
