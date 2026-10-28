using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Ordering.Domain;

public enum OrderStatus { Pending = 1, AwaitingPayment = 2, Confirmed = 3, Cancelled = 4, Refunded = 5, Delivered = 6 }

public class Order
{
    public Guid Id { get; init; }
    public long PublicOrderNumber { get; set; }

    /// <summary>
    /// Optimistic-concurrency token: Postgres's <c>xmin</c> system column, which changes on every update of the
    /// row (no column of our own, no trigger). An HTTP write (supplier/operator "mark delivered") runs outside the
    /// order's event partition, so EF adds <c>AND xmin = @loaded</c> to its UPDATE; a concurrent change makes it
    /// match no row and the request answers 409 instead of overwriting the newer state. Clients echo the value
    /// they loaded so a decision made on a stale screen is refused too.
    /// </summary>
    public uint Version { get; set; }

    /// <summary>The owning tenant (carried from the checkout attempt) — Fulfillment scopes inventory by it.</summary>
    public Guid TenantId { get; set; }
    public Guid? StorefrontId { get; set; }
    public Guid? UserId { get; set; }
    public required string Email { get; set; }
    public OrderStatus Status { get; set; }
    // A partial refund moves money but leaves the order Confirmed (a full refund → Refunded). This flag
    // lets the admin distinguish a partially-refunded order from an untouched one (rma_partial).
    public bool PartiallyRefunded { get; set; }
    // A chargeback (dispute) was opened against this order's payment (phase 2). The ledger has reversed
    // the sale + booked the dispute fee; this flag surfaces the disputed state to operators and the shopper.
    public bool Disputed { get; set; }
    public long NetMinor { get; set; }
    public long TaxMinor { get; set; }

    /// <summary>
    /// The shipping line's tax facts (ADR-0063 P1), carried from the checkout attempt.
    /// <c>Σ line TaxMinor + ShippingTaxMinor == TaxMinor</c> on every order placed since P1; the NotRecorded/0
    /// defaults mark an order placed before it (no backfill — ADR-0063 D18).
    /// </summary>
    public TaxCategory ShippingTaxCategory { get; set; }
    public int ShippingTaxRateBps { get; set; }
    public long ShippingTaxableBaseMinor { get; set; }
    public long ShippingTaxMinor { get; set; }
    public TaxExemptionReason ShippingExemptionReason { get; set; }

    /// <summary>The scheme the sale was taxed under (always Domestic today; NotRecorded before ADR-0063 P1).</summary>
    public TaxScheme TaxScheme { get; set; }

    /// <summary>Whether the prices CONTAINED the tax (inclusive regime, ADR-0038) or had it added (exclusive).</summary>
    public bool TaxInclusive { get; set; }

    /// <summary>
    /// ADR-0063 P1b: the storefront's tax jurisdiction the sale was taxed under (<c>CC</c> / <c>CC-RRR</c>, P2), or
    /// <c>""</c> when none was configured (and on rows written before P1b).
    /// </summary>
    public string TaxJurisdiction { get; set; } = string.Empty;

    /// <summary>
    /// ADR-0063 P1b: the country the tax strategy treated as the place of supply — the ship-to country unless the
    /// caller passed another destination; <c>""</c> on rows written before P1b.
    /// </summary>
    public string TaxDestinationCountry { get; set; } = string.Empty;

    public long ShippingMinor { get; set; }
    public long DiscountMinor { get; set; }

    /// <summary>The promotion share of <see cref="DiscountMinor"/> (ADR-0051); the remainder is the
    /// storefront-wide discount, which is a store setting rather than a promotion.</summary>
    public long PromotionDiscountMinor { get; set; }

    /// <summary>Comma-joined ids of every promotion that applied, or null when none did — the audit trail
    /// for why this order was charged what it was. Combinable promotions stack, so there may be several.</summary>
    public string? AppliedPromotionIds { get; set; }

    /// <summary>
    /// The NAMES of those promotions as they read at the moment of sale (ADR-0051), comma-joined and
    /// index-aligned with <see cref="AppliedPromotionIds"/>. Snapshotted because an id stops explaining a
    /// historical charge as soon as the promotion is renamed or deleted.
    /// </summary>
    public string? AppliedPromotionNames { get; set; }

    /// <summary>Whether a promotion zeroed the shipping charge on this order.</summary>
    public bool FreeShippingApplied { get; set; }

    /// <summary>The coupon code the shopper redeemed (canonical UPPERCASE), or null when none was used
    /// (ADR-0052). Snapshotted so a past order can be explained — and supported — without re-running the
    /// evaluator or joining the redemption table.</summary>
    public string? CouponCode { get; set; }

    public long GrossMinor { get; set; }
    public required string Currency { get; set; }
    public string? PaymentIntentId { get; set; }
    public string PaymentOption { get; set; } = "CreditCard";
    public string? PaymentInstrumentSummary { get; set; }
    public string PaymentProvider { get; set; } = "Stripe";
    public required string ShipName { get; set; }
    public required string ShipLine1 { get; set; }
    public required string ShipCity { get; set; }
    public string? ShipRegion { get; set; }
    public required string ShipPostcode { get; set; }
    public required string ShipCountry { get; set; }

    /// <summary>
    /// The shopper chose to collect this order at the fulfilling supplier's warehouse rather than have it
    /// shipped by a carrier (mt4 / ADR-0028). A collect order carries no carrier and zero
    /// <see cref="ShippingMinor"/>; the warehouse it is collected from is recorded in the Warehouse* fields.
    /// </summary>
    public bool CollectAtWarehouse { get; set; }
    public string? WarehouseName { get; set; }
    public string? WarehouseLine1 { get; set; }
    public string? WarehouseCity { get; set; }
    public string? WarehousePostcode { get; set; }
    public string? WarehouseCountry { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
    public List<OrderLine> Lines { get; init; } = [];
}

public class OrderLine
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public Guid ProductId { get; init; }
    public Guid? VariantId { get; init; }
    public string? VariantSku { get; set; }
    public required string Title { get; set; }
    public long UnitPriceMinor { get; init; }
    public long DiscountMinor { get; init; }

    /// <summary>
    /// The part of <see cref="DiscountMinor"/> that rides this line's RENEWALS when it is a subscription
    /// (ADR-0057) — allocated per LINE, like <see cref="DiscountMinor"/>. Renewals are charged
    /// <see cref="RenewalPriceMinor"/>; 0 means renewals charge list.
    /// </summary>
    public long RenewalDiscountMinor { get; init; }
    public int Quantity { get; init; }

    /// <summary>
    /// What each renewal of this subscription line costs, before any tax an exclusive store adds: the line total
    /// (<c>UnitPriceMinor × Quantity</c>) less the line's <see cref="RenewalDiscountMinor"/>, never negative — the
    /// same figure the cart's "renews at" preview shows (owner decision 2026-10-27, ADR-0057 amendment). A method,
    /// not a property, so EF never maps it.
    /// </summary>
    public long RenewalPriceMinor() => Math.Max(0, (UnitPriceMinor * Quantity) - RenewalDiscountMinor);

    /// <summary>How this line is sourced/delivered (ADR-0028, shared vocabulary).</summary>
    public FulfilmentType FulfilmentType { get; set; } = FulfilmentType.Unassigned;

    /// <summary>The supplier fulfilling this line (from its resolved offer) — drives dropship routing.</summary>
    public Guid? SupplierId { get; set; }

    /// <summary>How this line is charged (Phase 7): one-time / recurring / metered.</summary>
    public BillingMode BillingMode { get; set; } = BillingMode.OneTime;

    /// <summary>Charge cadence for recurring lines (Phase 7): Once / Monthly / Yearly.</summary>
    public BillingPeriod BillingPeriod { get; set; } = BillingPeriod.Once;

    /// <summary>
    /// Per-unit supplier cost of the line's supply offer, snapshotted at checkout from the same offer that
    /// set <see cref="SupplierId"/> (order currency by construction, ADR-0059). 0 = no supply offer / unknown.
    /// </summary>
    public long SupplierUnitCostMinor { get; init; }

    /// <summary>
    /// The supplier cost (COGS) actually ACCRUED for this whole line at confirmation, in the order's currency:
    /// <see cref="SupplierUnitCostMinor"/> × <see cref="Quantity"/> when the supplier was still approved,
    /// else 0. Reversals (RMA restock/write-off) value the returned goods from this booked figure — never by
    /// re-resolving offers — so a booking and its reversal can never diverge.
    /// </summary>
    public long BookedSupplierCostMinor { get; set; }

    /// <summary>
    /// The line's tax facts (ADR-0063 P1), carried verbatim from the checkout attempt line: category, rate, base
    /// (always NET of the tax — see <see cref="LineTax.TaxableBaseMinor"/>), its share of <see cref="Order.TaxMinor"/>
    /// (largest remainder) and the exemption reason. NotRecorded/0 on a pre-P1 order. An invoice takes these
    /// verbatim — it never recomputes base × rate (ADR-0063 § 8).
    /// </summary>
    public TaxCategory TaxCategory { get; init; }
    public int TaxRateBps { get; init; }
    public long TaxableBaseMinor { get; init; }
    public long TaxMinor { get; init; }
    public TaxExemptionReason ExemptionReason { get; init; }
}

/// <summary>
/// Why an order mutation over HTTP (admin cancel, supplier/operator mark-delivered) was refused. Every such
/// refusal is RFC 9457 problem+json carrying its member as the numeric <c>orderRefusal</c> extension — the same
/// shape as checkout's <see cref="CheckoutBlock"/> and the cart's <see cref="CartRefusal"/>. All are 409.
/// Crosses HTTP as a NUMBER — append new members, never renumber.
/// </summary>
public enum OrderRefusal
{
    None = 0,

    /// <summary>Cancel: the order is already cancelled.</summary>
    AlreadyCancelled = 1,

    /// <summary>Cancel: the order is paid (Confirmed) — it must be refunded instead.</summary>
    PaidOrderNotCancellable = 2,

    /// <summary>Mark delivered: only a Confirmed order can be marked delivered.</summary>
    NotConfirmed = 3,

    /// <summary>
    /// The order changed after the caller loaded it — its <c>Version</c> (xmin) no longer matches, either the one
    /// the client sent or the one read just before the save. Refresh and try again.
    /// </summary>
    ChangedConcurrently = 4,
}
