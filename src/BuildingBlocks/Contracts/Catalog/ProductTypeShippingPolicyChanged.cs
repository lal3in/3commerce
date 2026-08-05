namespace ThreeCommerce.BuildingBlocks.Contracts.Catalog;

/// <summary>
/// Published when a tenant's product-type shipping policy changes (which ProductType values require a
/// carrier). Ordering keeps a local copy (ADR-0008) so checkout can decide, per cart line, whether it is
/// shippable by the line's product type rather than only by its fulfilment type. RequiresShippingTypes is
/// the CSV of ProductType names that ship (e.g. "Physical,Bundle") — the same shape the policy persists.
/// </summary>
public record ProductTypeShippingPolicyChanged(
    Guid TenantId,
    string RequiresShippingTypes);
