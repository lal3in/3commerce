using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Ordering.Domain;
using Facts = ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;

namespace ThreeCommerce.Ordering.Infrastructure;

/// <summary>
/// Builds the rich <see cref="OrderConfirmed"/> from a confirmed <see cref="Order"/> aggregate — the ONE mapping
/// both the live confirmation path (<see cref="Consumers.OrderStatusConsumer"/>) and the operator backfill
/// (<c>tools/order-confirmed-backfill</c>, ADR-0060) use, so a re-delivered event is the event the order would
/// have produced at confirmation. Pure: everything comes from the aggregate and its lines, in
/// <see cref="Order.Lines"/> order.
/// </summary>
public static class OrderConfirmedFactory
{
    public static OrderConfirmed From(Order order)
    {
        // The refund basis (rma_disc): every consumer that has to value a line — Support's RMA snapshot
        // above all — needs the line's DISCOUNTED worth, not its shelf price. OrderLine.DiscountMinor
        // holds only the promotion share, so the storefront-wide remainder is apportioned here.
        var lineDiscounts = OrderLineDiscounts.For(order);
        return new OrderConfirmed(
            order.Id, order.TenantId, order.Email, order.GrossMinor, order.Currency,
            new ShipToInfo(order.ShipName, order.ShipLine1, order.ShipCity, order.ShipPostcode, order.ShipCountry, order.ShipRegion),
            order.Lines.Select((l, i) => new OrderLineInfo(
                l.ProductId, l.VariantId, l.SupplierId, l.Title, l.Quantity, l.FulfilmentType, l.BillingMode, l.UnitPriceMinor,
                lineDiscounts[i],
                // Invoicing facts (ADR-0063 P5): the line's id — what a credit note attributes a return to — and its
                // persisted P1 tax facts, verbatim (never recomputed: the document must show what was charged).
                LineId: l.Id,
                TaxCategory: Category(l.TaxCategory),
                TaxRateBps: l.TaxRateBps,
                TaxableBaseMinor: l.TaxableBaseMinor,
                TaxMinor: l.TaxMinor,
                ExemptionReason: Reason(l.ExemptionReason))).ToList(),
            StorefrontId: order.StorefrontId,
            PublicOrderNumber: order.PublicOrderNumber,
            NetMinor: order.NetMinor,
            DiscountMinor: order.DiscountMinor,
            ShippingMinor: order.ShippingMinor,
            TaxMinor: order.TaxMinor,
            TaxInclusive: order.TaxInclusive,
            TaxJurisdiction: order.TaxJurisdiction,
            TaxDestinationCountry: order.TaxDestinationCountry,
            TaxScheme: (Facts.TaxScheme)(int)order.TaxScheme,
            BillTo: BillTo(order),
            Buyer: Buyer(order),
            ShippingTax: new LineTaxInfo(
                Category(order.ShippingTaxCategory), order.ShippingTaxRateBps, order.ShippingTaxableBaseMinor,
                order.ShippingTaxMinor, Reason(order.ShippingExemptionReason)),
            // An order is created from its checkout attempt in the very transaction that confirms it, once the payment
            // is captured (OrderStatusConsumer on CheckoutCompleted), so its CreatedAt IS the capture time — the date a
            // prepaid sale's invoice carries (ADR-0063 § 5). Read from the row, so a backfilled event repeats it.
            CapturedAt: order.CreatedAt);
    }

    // Ordering's P1 enums and the published vocabulary share their numeric values (pinned by
    // InvoicingContractFactsTests), so the facts cross by value.
    private static Facts.TaxCategory Category(TaxCategory category) => (Facts.TaxCategory)(int)category;

    private static Facts.TaxExemptionReason Reason(TaxExemptionReason reason) => (Facts.TaxExemptionReason)(int)reason;

    // An order confirmed before P3 has no bill-to (D18: not backfilled). It is published as null — "not recorded" —
    // rather than guessed from the ship-to, so a document built from it can tell the difference.
    private static BillToInfo? BillTo(Order order)
    {
        if (order.BillName is not { } name || order.BillLine1 is not { } line1 || order.BillCity is not { } city
            || order.BillPostcode is not { } postcode || order.BillCountry is not { } country)
        {
            return null;
        }

        return new BillToInfo(name, line1, city, postcode, country, order.BillRegion);
    }

    // No business block = a consumer purchase (P3).
    private static BuyerInfo? Buyer(Order order) =>
        string.IsNullOrWhiteSpace(order.BuyerCompanyName)
            ? null
            : new BuyerInfo(order.BuyerCompanyName, order.BuyerTaxId, order.BuyerTaxIdScheme, order.BuyerTaxIdJurisdiction);
}
