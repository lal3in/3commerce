namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Local read copy of a storefront's tax config, fed by Catalog's StorefrontConfigChanged events
/// (ADR-0008: no cross-service queries). Checkout resolves the rate to charge by the cart's currency.
/// </summary>
public class StorefrontTaxCopy
{
    public Guid StorefrontId { get; init; }
    public Guid TenantId { get; set; }
    public required string Currency { get; set; }

    /// <summary>Tenant-set tax rate in basis points (1000 = 10%).</summary>
    public int TaxRateBasisPoints { get; set; }

    /// <summary>
    /// Storefront-wide discount in basis points (0–10000; 0 = none). Checkout deducts it from the items'
    /// subtotal only (never shipping, never tax), applied after the per-line offer/catalog price resolves.
    /// </summary>
    public int DiscountBasisPoints { get; set; }

    /// <summary>Active/Preview storefronts only participate in checkout tax resolution.</summary>
    public bool IsLive { get; set; }

    /// <summary>
    /// ADR-0038: inclusive regimes (AU GST / EU VAT) — the tenant's shelf price already CONTAINS
    /// the tax; checkout extracts it informationally and charges the listed amount. Exclusive
    /// regimes (US sales tax) add it on top.
    /// </summary>
    public bool TaxInclusive { get; set; }

    /// <summary>
    /// Ship-to allowlist (ISO 3166-1 alpha-2) projected from Catalog. EMPTY = ships worldwide;
    /// non-empty = checkout rejects a ship-to country not in this list.
    /// </summary>
    public List<string> ShipToCountries { get; set; } = [];

    /// <summary>
    /// ADR-0063 P2: where the storefront's sales are taxed (ISO country + optional region, e.g. <c>AU</c>,
    /// <c>US-CA</c>), projected from Catalog. Empty = not configured (and on copies projected from a
    /// pre-P2 message). Input for the per-line tax breakdown (P1/P1b); not used by checkout yet.
    /// </summary>
    public string TaxJurisdiction { get; set; } = string.Empty;

    /// <summary>ADR-0063 P2: the seller establishment key the storefront sells from; empty = not bound.</summary>
    public string SellerEstablishmentKey { get; set; } = string.Empty;
}
