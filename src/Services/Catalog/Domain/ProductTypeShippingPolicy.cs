using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// Per-tenant policy for which <see cref="ProductType"/> values require shipping / a carrier (mt4_3).
/// Physical goods ship; digital/service/subscription/usage do not — but a tenant can tailor this
/// (e.g. treat a Bundle as shippable). Drives the publish-readiness shipping-dimension gate: a product
/// whose type requires shipping can't be published until its variants carry item + package dimensions.
/// Stored as a CSV of <see cref="ProductType"/> names in one row per tenant.
/// </summary>
public sealed class ProductTypeShippingPolicy
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }

    /// <summary>CSV of the ProductType names that require shipping (e.g. "Physical,Bundle").</summary>
    public string RequiresShippingTypes { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The default when a tenant has set no explicit policy: only Physical goods ship.</summary>
    public static readonly IReadOnlySet<ProductType> DefaultTypes = new HashSet<ProductType> { ProductType.Physical };

    private ProductTypeShippingPolicy() { }

    public static ProductTypeShippingPolicy Create(Guid tenantId, DateTimeOffset now)
    {
        var policy = new ProductTypeShippingPolicy { Id = Guid.CreateVersion7(), TenantId = tenantId };
        policy.SetTypes(DefaultTypes, now);
        return policy;
    }

    /// <summary>The set of product types this policy marks as shippable.</summary>
    public IReadOnlySet<ProductType> Types => Parse(RequiresShippingTypes);

    public bool RequiresShipping(ProductType type) => Types.Contains(type);

    public void SetTypes(IEnumerable<ProductType> types, DateTimeOffset now)
    {
        RequiresShippingTypes = string.Join(
            ',', types.Distinct().OrderBy(t => (int)t).Select(t => t.ToString()));
        UpdatedAt = now;
    }

    private static IReadOnlySet<ProductType> Parse(string csv)
    {
        var set = new HashSet<ProductType>();
        if (string.IsNullOrWhiteSpace(csv))
        {
            return set;
        }

        foreach (var token in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<ProductType>(token, out var type))
            {
                set.Add(type);
            }
        }

        return set;
    }
}
