namespace ThreeCommerce.Identity.Domain;

public class Address
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }

    /// <summary>Denormalized from the owning user for tenant RLS (ADR-0024).</summary>
    public Guid TenantId { get; init; }

    public AddressPurpose Purpose { get; set; } = AddressPurpose.Both;
    public bool IsDefault { get; set; }
    public required string Name { get; set; }
    public required string Line1 { get; set; }
    public string? Line2 { get; set; }
    public required string City { get; set; }
    public string? Region { get; set; }
    public required string Postcode { get; set; }
    /// <summary>ISO 3166-1 alpha-2.</summary>
    public required string Country { get; set; }

    public bool CanBeUsedFor(AddressPurpose purpose) =>
        Purpose == AddressPurpose.Both || Purpose == purpose;
}

public enum AddressPurpose
{
    Billing = 1,
    Shipping = 2,
    Both = 3,
}
