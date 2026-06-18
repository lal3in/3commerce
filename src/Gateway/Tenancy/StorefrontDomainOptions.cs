namespace ThreeCommerce.Gateway.Tenancy;

public sealed class StorefrontDomainOptions
{
    public string? DefaultTenantId { get; set; }

    public string? DefaultStorefrontId { get; set; }

    public List<StorefrontDomainMapping> Domains { get; set; } = [];
}

public sealed class StorefrontDomainMapping
{
    public required string Host { get; set; }

    public required string TenantId { get; set; }

    public string? StorefrontId { get; set; }

    public bool Canonical { get; set; }

    public string? CanonicalHost { get; set; }
}
