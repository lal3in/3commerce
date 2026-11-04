using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Contracts.Tax;
using ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;

namespace ThreeCommerce.BuildingBlocks.Contracts.Ordering;

/// <summary>
/// Rich domain event published by the Order aggregate owner (Ordering's status updater)
/// once payment is captured. Consumed by Notifications (email) and Fulfillment (shipments).
/// <para>
/// INVOICING FACTS (ADR-0063 § 6.2, P5) are APPENDED, every one with a default, so an event published before they
/// existed still deserializes and every existing consumer reads exactly what it read before. A replayed pre-P5 event
/// carries <see cref="StorefrontId"/> null, zero totals, <see cref="TaxScheme.NotRecorded"/>, no bill-to, no buyer,
/// no shipping tax and no <see cref="CapturedAt"/>: a consumer must treat that as "facts unknown", never as a zero-tax
/// sale. Money identity of a P5 event: <c>NetMinor − DiscountMinor + ShippingMinor (+ TaxMinor when not
/// TaxInclusive) == AmountMinor</c>, and <c>Σ line TaxMinor + ShippingTax.TaxMinor == TaxMinor</c> (P1).
/// </para>
/// </summary>
/// <param name="AmountMinor">What the shopper was charged (the order's gross).</param>
/// <param name="StorefrontId">The storefront the order was placed on.</param>
/// <param name="PublicOrderNumber">The storefront's human order number (gap-tolerant reference, not a tax-invoice number — ADR-0063 § 4.4).</param>
/// <param name="NetMinor">The PRE-discount goods subtotal (Σ listed unit price × quantity).</param>
/// <param name="DiscountMinor">The whole order discount (promotions + storefront-wide); the lines' <see cref="OrderLineInfo.DiscountMinor"/> sum to it.</param>
/// <param name="ShippingMinor">The shipping charged.</param>
/// <param name="TaxMinor">The tax charged (inside the prices on an inclusive store, added on an exclusive one — ADR-0038).</param>
/// <param name="TaxInclusive">Whether the storefront's prices contain the tax.</param>
/// <param name="TaxJurisdiction">The storefront's tax jurisdiction as applied (<c>""</c> | <c>CC</c> | <c>CC-RRR</c>, ADR-0063 P2/P1b).</param>
/// <param name="TaxDestinationCountry">The destination country the tax was resolved for (ISO-2; <c>""</c> when not recorded, P1b).</param>
/// <param name="TaxScheme">The scheme the sale fell under (<see cref="TaxScheme.Domestic"/> / <see cref="TaxScheme.Oss"/> …).</param>
/// <param name="BillTo">The bill-to (invoice) address (P3); null on orders placed before it was captured.</param>
/// <param name="Buyer">The business purchase block (P3); null for a consumer purchase.</param>
/// <param name="ShippingTax">The shipping line's tax facts (P1); null on a pre-P5 event.</param>
/// <param name="CapturedAt">When the payment was captured and the order confirmed — the invoice date (ADR-0063 § 5).</param>
public record OrderConfirmed(
    Guid OrderId,
    Guid TenantId,
    string Email,
    long AmountMinor,
    string Currency,
    ShipToInfo ShipTo,
    IReadOnlyList<OrderLineInfo> Lines,
    Guid? StorefrontId = null,
    long PublicOrderNumber = 0,
    long NetMinor = 0,
    long DiscountMinor = 0,
    long ShippingMinor = 0,
    long TaxMinor = 0,
    bool TaxInclusive = false,
    string TaxJurisdiction = "",
    string TaxDestinationCountry = "",
    TaxScheme TaxScheme = TaxScheme.NotRecorded,
    BillToInfo? BillTo = null,
    BuyerInfo? Buyer = null,
    LineTaxInfo? ShippingTax = null,
    DateTimeOffset? CapturedAt = null);

public record ShipToInfo(string Name, string Line1, string City, string Postcode, string Country, string? Region = null);

/// <summary>The bill-to (invoice) address of an order (ADR-0063 P3). Defaults to the ship-to address at checkout.</summary>
public record BillToInfo(string Name, string Line1, string City, string Postcode, string Country, string? Region = null);

/// <summary>
/// The business purchase block of an order (ADR-0063 P3): the buyer's company and, optionally, its tax identifier
/// (format-checked at checkout, not externally validated — VIES is phase 2). Its presence marks a business purchase;
/// the amount charged is unchanged by it (no reverse charge, § 12 D5).
/// </summary>
/// <param name="TaxIdScheme">The register the tax ID belongs to; crosses the wire as a number.</param>
/// <param name="TaxIdJurisdiction">The issuing jurisdiction (ISO country, or <c>US-CA</c> for a US state ID).</param>
public record BuyerInfo(string CompanyName, string? TaxId = null, TaxIdScheme? TaxIdScheme = null, string? TaxIdJurisdiction = null);

/// <summary>
/// The tax facts of one taxed component (the shipping line) — the P1 shape. <paramref name="TaxableBaseMinor"/> is
/// always NET of the tax, so <c>TaxableBaseMinor + TaxMinor</c> is what the shopper paid for the component.
/// </summary>
public record LineTaxInfo(
    TaxCategory Category,
    int RateBps,
    long TaxableBaseMinor,
    long TaxMinor,
    TaxExemptionReason ExemptionReason = TaxExemptionReason.None);

/// <summary>
/// One confirmed order line. <paramref name="UnitPriceMinor"/> is the LISTED per-unit price;
/// <paramref name="DiscountMinor"/> is this line's whole allocated share of the order's discount
/// (promotion + storefront-wide), so the line's real selling value is
/// <c>UnitPriceMinor × Quantity − DiscountMinor</c> — the basis a refund must be computed on (rma_disc).
/// The allocations across an order's lines sum to <c>Order.DiscountMinor</c> exactly.
/// <para>
/// APPENDED with a default (contracts version additively): a consumer replaying an event published
/// before this field existed reads 0, i.e. "no discount known" — the pre-change behaviour.
/// </para>
/// <para>
/// APPENDED for invoicing (ADR-0063 P5): <paramref name="LineId"/> is Ordering's order-line id — the key a credit
/// note attributes a returned line to (refunds carry it back in <c>RefundLine.LineId</c>) — and the line's P1 tax
/// facts. A pre-P5 event reads <see cref="Guid.Empty"/> and <see cref="TaxCategory.NotRecorded"/>.
/// </para>
/// </summary>
/// <param name="TaxableBaseMinor">The line's base, NET of its tax (P1 convention).</param>
public record OrderLineInfo(
    Guid ProductId, Guid? VariantId, Guid? SupplierId, string Title, int Quantity,
    FulfilmentType FulfilmentType, BillingMode BillingMode, long UnitPriceMinor,
    long DiscountMinor = 0,
    Guid LineId = default,
    TaxCategory TaxCategory = TaxCategory.NotRecorded,
    int TaxRateBps = 0,
    long TaxableBaseMinor = 0,
    long TaxMinor = 0,
    TaxExemptionReason ExemptionReason = TaxExemptionReason.None);
