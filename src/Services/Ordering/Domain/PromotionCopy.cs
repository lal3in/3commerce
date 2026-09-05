using ThreeCommerce.BuildingBlocks.Contracts.Catalog;

namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Local read copy of a Catalog Promotion (ADR-0051 / ADR-0008), kept current via PromotionChanged
/// events. Checkout and <c>GET /cart/summary</c> evaluate promotions from these without a cross-service
/// query into Catalog.
/// </summary>
public class PromotionCopy
{
    /// <summary>The Catalog promotion's id — the projection key.</summary>
    public Guid PromotionId { get; init; }

    /// <summary>Owning tenant; a promotion never crosses tenants.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Restricts the promotion to one storefront; null = all storefronts of <see cref="Currency"/>.</summary>
    public Guid? StorefrontId { get; set; }

    /// <summary>Shopper-visible label rendered on the cart/checkout summary.</summary>
    public string Name { get; set; } = "";

    /// <summary>The currency the thresholds and the fixed discount are denominated in. Defaults to "" so a
    /// legacy row can never accidentally match a cart currency — the guard is the no-FX invariant
    /// (ADR-0041): a promotion in EUR must never apply to an AUD cart.</summary>
    public string Currency { get; set; } = "";

    /// <summary>Whether the promotion measures/discounts the whole cart or a single product's lines.</summary>
    public PromotionScopeKind Scope { get; set; } = PromotionScopeKind.Storefront;

    /// <summary>The measured/discounted product; set iff <see cref="Scope"/> is Product.</summary>
    public Guid? ProductId { get; set; }

    /// <summary>Money threshold in minor units on the scope's item value; 0 = no money threshold.</summary>
    public long MinimumAmountMinor { get; set; }

    /// <summary>Quantity threshold on the scope's unit count; 0 = no quantity threshold. Both set ⇒ AND.</summary>
    public int MinimumQuantity { get; set; }

    /// <summary>Whether winning this promotion zeroes the cart's shipping charge.</summary>
    public bool GrantsFreeShipping { get; set; }

    /// <summary>Percentage off the scope base, 0–100. Mutually exclusive with <see cref="DiscountAmountMinor"/>.</summary>
    public int PercentOff { get; set; }

    /// <summary>Fixed discount in minor units, clamped to the scope base at evaluation.</summary>
    public long DiscountAmountMinor { get; set; }

    /// <summary>True = stacks with other combinable promotions; false = Exclusive (only this one applies).</summary>
    public bool Combinable { get; set; }

    /// <summary>Whether the source promotion is Active; an inactive copy never applies.</summary>
    public bool Active { get; set; }

    /// <summary>Inclusive start of the active window (UTC); null = open-ended (always started).</summary>
    public DateTimeOffset? ActiveFrom { get; set; }

    /// <summary>Inclusive end of the active window (UTC); null = open-ended (never expires).</summary>
    public DateTimeOffset? ActiveUntil { get; set; }

    /// <summary>
    /// The coupon code that gates this promotion (ADR-0052), projected from Catalog in its canonical
    /// trimmed-UPPERCASE form. Null/empty = the promotion is AUTOMATIC and applies without a code
    /// (the ADR-0051 behaviour). Matched case-insensitively against what the shopper typed.
    /// </summary>
    public string? Code { get; set; }

    /// <summary>Total redemptions this promotion may grant; null = unlimited. Single-use is 1.</summary>
    public int? MaxRedemptions { get; set; }

    /// <summary>Redemptions one customer may take; null = unlimited.</summary>
    public int? MaxRedemptionsPerCustomer { get; set; }

    /// <summary>
    /// How many redemptions are currently held (Reserved or Confirmed) against this promotion.
    /// <para>
    /// ORDERING-OWNED state, NOT projected: Catalog authors the promotion, Ordering is where a coupon is
    /// actually spent, so the counter lives here and the PromotionChanged projection consumer
    /// deliberately never assigns it (a re-projection must not reset the cap). It is incremented and
    /// decremented ONLY through <c>PromotionRedemptionService</c>, whose conditional UPDATE is what makes
    /// the cap race-safe under concurrent checkouts.
    /// </para>
    /// </summary>
    public int RedeemedCount { get; set; }

    /// <summary>Whether this promotion only applies when the shopper enters <see cref="Code"/>.</summary>
    public bool IsCouponGated => !string.IsNullOrEmpty(Code);

    /// <summary>
    /// Whether <paramref name="enteredCode"/> unlocks this promotion. An AUTOMATIC promotion (no code)
    /// always passes — it is not code-gated. A code-gated one passes only on a case-insensitive match, so
    /// a shopper typing "welcome10" gets the promotion stored as "WELCOME10".
    /// </summary>
    public bool AcceptsCode(string? enteredCode) =>
        !IsCouponGated || string.Equals(Code, enteredCode?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this promotion can apply for <paramref name="storefrontId"/> in
    /// <paramref name="currency"/> at <paramref name="now"/>: Active, tenant-matching, storefront-matching
    /// (or all-storefront), currency-matching, and inside the [ActiveFrom, ActiveUntil] window (null bounds
    /// are open-ended). The currency guard is the no-FX invariant — amounts are never converted.</summary>
    public bool IsEffectiveFor(Guid tenantId, Guid storefrontId, string currency, DateTimeOffset now) =>
        Active
        && TenantId == tenantId
        && (StorefrontId is null || StorefrontId == storefrontId)
        && string.Equals(Currency, currency, StringComparison.OrdinalIgnoreCase)
        && (ActiveFrom is null || ActiveFrom <= now)
        && (ActiveUntil is null || now <= ActiveUntil);
}
