namespace ThreeCommerce.BuildingBlocks.Contracts.Catalog;

/// <summary>
/// What a threshold promotion measures and what it discounts (ADR-0051). Mirrors Catalog's
/// <c>PromotionScope</c>; the contract owns its own enum because BuildingBlocks is the only assembly
/// both Catalog and Ordering may share (Catalog maps its domain enum onto this one).
/// </summary>
public enum PromotionScopeKind
{
    /// <summary>Measures the whole cart's item value/quantity and discounts every item line.</summary>
    Storefront = 1,

    /// <summary>Measures one product's item value/quantity and discounts only that product's lines.</summary>
    Product = 2,
}

/// <summary>
/// Published when a threshold promotion (ADR-0051) is created or changed. Feeds Ordering's local
/// PromotionCopy so checkout and <c>GET /cart/summary</c> can evaluate promotions without a
/// cross-service query into Catalog (ADR-0008). Carries Active so a deactivated promotion is excluded
/// from evaluation as soon as the projection lands.
/// <para>
/// MinimumAmountMinor and DiscountAmountMinor are integer minor units denominated in Currency and are
/// never converted (strict no-FX posture, ADR-0041): a promotion whose Currency differs from the cart's
/// simply never applies. When both MinimumAmountMinor and MinimumQuantity are set they are ANDed.
/// </para>
/// </summary>
public record PromotionChanged(
    Guid PromotionId,
    Guid TenantId,
    // Null = the promotion applies to every storefront of Currency.
    Guid? StorefrontId,
    string Name,
    string Currency,
    PromotionScopeKind Scope,
    // Set iff Scope is Product: the product whose lines are measured and discounted.
    Guid? ProductId,
    // 0 = no money threshold / no quantity threshold. At least one is set; both set ⇒ AND.
    long MinimumAmountMinor,
    int MinimumQuantity,
    // Rewards: free shipping and/or a discount that is either PercentOff (0-100) or DiscountAmountMinor.
    bool GrantsFreeShipping,
    int PercentOff,
    long DiscountAmountMinor,
    // true = stacks with other combinable promotions; false = Exclusive (only this one applies).
    bool Combinable,
    bool Active,
    // Inclusive active window (UTC). Appended, back-compatible defaults — a copy from before these
    // fields carries an open-ended window (always started, never expires).
    DateTimeOffset? ActiveFrom = null,
    DateTimeOffset? ActiveUntil = null);
