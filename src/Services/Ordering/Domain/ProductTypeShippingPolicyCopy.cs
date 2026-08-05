using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Local read copy of a tenant's Catalog product-type shipping policy (ADR-0008), kept current via
/// ProductTypeShippingPolicyChanged. Checkout uses it to decide, per cart line, whether the line ships
/// by its product type. Stored as the CSV of ProductType names that require shipping (the policy's own
/// persisted shape). A tenant with no copy falls back to the fulfilment-type gate.
/// </summary>
public class ProductTypeShippingPolicyCopy
{
    public Guid TenantId { get; init; }
    public string RequiresShippingTypes { get; set; } = string.Empty;

    public bool RequiresShipping(ProductType type)
    {
        if (string.IsNullOrWhiteSpace(RequiresShippingTypes))
        {
            return false;
        }

        foreach (var token in RequiresShippingTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<ProductType>(token, out var parsed) && parsed == type)
            {
                return true;
            }
        }

        return false;
    }
}
