namespace ThreeCommerce.Identity.Domain;

public class Address
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public required string Name { get; set; }
    public required string Line1 { get; set; }
    public string? Line2 { get; set; }
    public required string City { get; set; }
    public required string Postcode { get; set; }
    /// <summary>ISO 3166-1 alpha-2.</summary>
    public required string Country { get; set; }
}
