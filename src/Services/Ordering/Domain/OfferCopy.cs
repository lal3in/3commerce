using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Local read copy of a Catalog Offer (ADR-0028 / ADR-0008), kept current via OfferChanged events.
/// Checkout resolves each line's fulfilment type from these without a cross-service query.
/// </summary>
public class OfferCopy
{
    public Guid OfferId { get; init; }
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? VariantId { get; set; }
    public Guid SupplierId { get; set; }
    public FulfilmentType FulfilmentType { get; set; }

    /// <summary>The product's nature (ADR-0028), projected from Catalog so checkout can apply the tenant's
    /// ProductType shipping policy per line. 0 = unknown (a copy from before this field, or a missing
    /// product) — checkout then falls back to the fulfilment-type shipping gate for that line.</summary>
    public ProductType ProductType { get; set; }

    public PricingModel PricingModel { get; set; } = PricingModel.OneTime;
    public BillingPeriod BillingPeriod { get; set; } = BillingPeriod.Once;
    public int Priority { get; set; }
    public bool Active { get; set; }

    /// <summary>Per-unit supplier cost (COGS, phase 1), minor units in <see cref="Currency"/>. 0 = unknown.</summary>
    public long SupplierCostMinor { get; set; }

    /// <summary>The offer's currency — its price AND its <see cref="SupplierCostMinor"/>. An offer only
    /// supplies (and costs) a line sold in this currency (ADR-0059 coverage), so COGS never crosses
    /// currencies (no FX in this codebase).</summary>
    public string Currency { get; set; } = "";

    /// <summary>The offer's authoritative selling price (minor units in <see cref="Currency"/>). When this
    /// offer is effective for a line's storefront + currency at checkout, it sets the line's charged price
    /// instead of the catalog SellingPriceMinor. 0 = no offer price projected (keep the catalog price).</summary>
    public long PriceMinor { get; set; }

    /// <summary>Restricts the offer price to a single storefront; null = all storefronts of <see cref="Currency"/>.</summary>
    public Guid? StorefrontId { get; set; }

    /// <summary>Inclusive start of the active window (UTC); null = open-ended.</summary>
    public DateTimeOffset? ActiveFrom { get; set; }

    /// <summary>Inclusive end of the active window (UTC); null = open-ended.</summary>
    public DateTimeOffset? ActiveUntil { get; set; }

    /// <summary>Whether this offer's price applies for <paramref name="storefrontId"/> in
    /// <paramref name="currency"/> at <paramref name="now"/>: Active, storefront-matching (or all-storefront),
    /// currency-matching, and within the [ActiveFrom, ActiveUntil] window (null bounds are open-ended).</summary>
    public bool IsEffectiveFor(Guid storefrontId, string currency, DateTimeOffset now) =>
        Active
        && (StorefrontId is null || StorefrontId == storefrontId)
        && string.Equals(Currency, currency, StringComparison.OrdinalIgnoreCase)
        && (ActiveFrom is null || ActiveFrom <= now)
        && (ActiveUntil is null || now <= ActiveUntil);

    /// <summary>How this offer's line is charged (Phase 7): recurring for subscriptions, metered for usage.</summary>
    public BillingMode BillingMode => PricingModel switch
    {
        PricingModel.Subscription => BillingMode.Recurring,
        PricingModel.UsageBased => BillingMode.Metered,
        _ => BillingMode.OneTime,
    };
}

/// <summary>Offer resolution for a cart/order line (ADR-0028, ADR-0047, ADR-0048, ADR-0059).
///
/// Approval-gated availability (DECISION A, strict): an offer backed by a supplier that is not in the
/// approved set never counts — not for pricing, not for supply, not for COGS.</summary>
public static class OfferResolution
{
    /// <summary>
    /// THE offer of a line sold on <paramref name="storefrontId"/> in <paramref name="currency"/> at
    /// <paramref name="now"/> — one offer decides its price, its supply (supplier, fulfilment type, billing
    /// mode) and its supplier cost (owner, 2026-10-15; ADR-0059 Amendment 2).
    /// <para>
    /// When an offer sets the line's price (<see cref="ResolvePricingOffer"/>: effective, in-window, priced,
    /// approved — unchanged, so no shelf price moves), that offer is the line's offer. Otherwise the catalogue
    /// price stands and the line's offer is <see cref="ResolveSupplyOffer"/> over the sell gate's set. Both rank
    /// the same way: variant-specific → storefront-scoped over all-storefront → lowest
    /// <see cref="OfferCopy.Priority"/>. Every pricing candidate is also a supply candidate (effective ⇒
    /// covering), so the result always lies in the gate's set.
    /// </para>
    /// <para>
    /// Null = no eligible offer: an offerless line (catalogue price, <c>Unassigned</c>, no supplier, one-time).
    /// </para>
    /// </summary>
    public static OfferCopy? ResolveLineOffer(
        IEnumerable<OfferCopy> offers, Guid tenantId, Guid productId, Guid? variantId,
        Guid storefrontId, string currency, DateTimeOffset now, IReadOnlySet<Guid> approvedSupplierIds)
    {
        var list = offers as IReadOnlyCollection<OfferCopy> ?? offers.ToList();
        return ResolvePricingOffer(list, tenantId, productId, variantId, storefrontId, currency, now, approvedSupplierIds)
            ?? ResolveSupplyOffer(list, tenantId, productId, variantId, storefrontId, currency, approvedSupplierIds);
    }

    /// <summary>
    /// The offer that SUPPLIES a line when no offer sets its price — callers use <see cref="ResolveLineOffer"/>,
    /// which prefers the price-setting offer so price, supply and cost are one offer. The candidates are exactly
    /// the sell gate's set (<see cref="IsSupplyAvailable"/>, ADR-0059): offers that <see cref="Covers"/> the line
    /// on this storefront (or all storefronts) in this currency AND whose supplier is approved — so an offer for
    /// another store or another currency can never supply the line. Ranked as the price is: variant-specific →
    /// storefront-scoped over all-storefront → lowest <see cref="OfferCopy.Priority"/> → lowest
    /// <see cref="OfferCopy.OfferId"/> (deterministic tie-break).
    /// <para>
    /// Not interchangeable with <see cref="ResolvePricingOffer"/> on its own: that one skips price-less or
    /// out-of-window offers (a subscription offer whose catalogue price stands has no price).
    /// </para>
    /// </summary>
    public static OfferCopy? ResolveSupplyOffer(
        IEnumerable<OfferCopy> offers, Guid tenantId, Guid productId, Guid? variantId,
        Guid storefrontId, string currency, IReadOnlySet<Guid> approvedSupplierIds) =>
        offers
            .Where(o => Covers(o, tenantId, productId, variantId, storefrontId, currency)
                && approvedSupplierIds.Contains(o.SupplierId))
            .OrderByDescending(o => o.VariantId == variantId)
            .ThenByDescending(o => o.StorefrontId == storefrontId)
            .ThenBy(o => o.Priority)
            .ThenBy(o => o.OfferId)
            .FirstOrDefault();

    /// <summary>
    /// Whether <paramref name="offer"/> COVERS a line sold on <paramref name="storefrontId"/> in
    /// <paramref name="currency"/> — Catalog's availability predicate (ADR-0048, <c>ProductsEndpoints</c>
    /// listing/detail), duplicated here on purpose (no shared domain logic): an ACTIVE offer of the tenant
    /// for this product at the line's grain (variant-specific or product-level), denominated in the line's
    /// currency, scoped to this storefront or to all storefronts. The active WINDOW is deliberately not part
    /// of coverage — Catalog does not apply it there either (it only gates the offer PRICE).
    /// </summary>
    public static bool Covers(
        OfferCopy offer, Guid tenantId, Guid productId, Guid? variantId, Guid storefrontId, string currency) =>
        offer.Active
        && offer.TenantId == tenantId
        && offer.ProductId == productId
        && (offer.VariantId == variantId || offer.VariantId == null)
        && string.Equals(offer.Currency, currency, StringComparison.OrdinalIgnoreCase)
        && (offer.StorefrontId is null || offer.StorefrontId == storefrontId);

    /// <summary>
    /// Approval-gated availability, evaluated exactly as Catalog evaluates it (ADR-0048 + ADR-0059: shown ==
    /// sellable == charged): a line is sellable on this storefront in this currency when NO offer covers it
    /// (an offerless line is governed by the catalogue price — the scope guard) or when at least one covering
    /// offer is from an approved supplier. An offer approved only for another storefront or another currency
    /// therefore neither hides nor unlocks the line here.
    /// </summary>
    public static bool IsSupplyAvailable(
        IEnumerable<OfferCopy> offers, Guid tenantId, Guid productId, Guid? variantId,
        Guid storefrontId, string currency, IReadOnlySet<Guid> approvedSupplierIds)
    {
        var covered = false;
        foreach (var offer in offers)
        {
            if (!Covers(offer, tenantId, productId, variantId, storefrontId, currency))
            {
                continue;
            }

            if (approvedSupplierIds.Contains(offer.SupplierId))
            {
                return true;
            }

            covered = true;
        }

        return !covered;
    }

    /// <summary>The fulfilment type of the line's supply offer (<see cref="ResolveSupplyOffer"/>);
    /// <c>Unassigned</c> when no offer is eligible.</summary>
    public static FulfilmentType ResolveFulfilment(
        IEnumerable<OfferCopy> offers, Guid tenantId, Guid productId, Guid? variantId,
        Guid storefrontId, string currency, IReadOnlySet<Guid> approvedSupplierIds) =>
        ResolveSupplyOffer(offers, tenantId, productId, variantId, storefrontId, currency, approvedSupplierIds)?.FulfilmentType
            ?? FulfilmentType.Unassigned;

    /// <summary>
    /// Picks the offer whose price should be CHARGED for a line on a specific storefront (offer-as-price,
    /// ADR-0028): the same grain precedence as <see cref="ResolveSupplyOffer"/> (a variant-specific offer beats a
    /// product-level one; lowest priority wins), but filtered to offers that are effective for the line's
    /// storefront + currency at <paramref name="now"/> (active, in-window, storefront-matching, and carrying
    /// a non-zero price). A storefront-scoped offer is preferred over an all-storefront one at the same grain.
    /// Null = no effective offer → checkout keeps the catalog price. When <paramref name="approvedSupplierIds"/>
    /// is supplied, an unapproved supplier's offer is excluded (DECISION A) so it can never set the price.
    /// </summary>
    public static OfferCopy? ResolvePricingOffer(
        IEnumerable<OfferCopy> offers, Guid tenantId, Guid productId, Guid? variantId,
        Guid storefrontId, string currency, DateTimeOffset now,
        IReadOnlySet<Guid>? approvedSupplierIds = null) =>
        offers
            .Where(o => o.TenantId == tenantId && o.ProductId == productId
                && (o.VariantId == variantId || o.VariantId == null)
                && o.PriceMinor > 0
                && (approvedSupplierIds is null || approvedSupplierIds.Contains(o.SupplierId))
                && o.IsEffectiveFor(storefrontId, currency, now))
            .OrderByDescending(o => o.VariantId == variantId)
            .ThenByDescending(o => o.StorefrontId == storefrontId)
            .ThenBy(o => o.Priority)
            .FirstOrDefault();
}
