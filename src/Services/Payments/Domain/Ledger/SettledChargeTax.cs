using ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;

namespace ThreeCommerce.Payments.Domain.Ledger;

/// <summary>The tax facts of one settled renewal / usage charge, in the P1 shape the settled-charge events publish.</summary>
/// <param name="Line">The charged line with its category, rate, base, tax and exemption.</param>
/// <param name="Scheme">Domestic (Payments charges the storefront's own single rate), NotRecorded when the rate was unknown.</param>
/// <param name="TaxInclusive">Whether the price contains the tax.</param>
/// <param name="NetMinor">Revenue posted (gross − tax).</param>
/// <param name="TaxMinor">Tax posted.</param>
/// <param name="GrossMinor">What was charged.</param>
public sealed record SettledChargeTaxFacts(
    SettledChargeLine Line, TaxScheme Scheme, bool TaxInclusive, long NetMinor, long TaxMinor, long GrossMinor);

/// <summary>
/// Turns the split a renewal / usage charge was charged and posted with (<see cref="RecurringChargeTaxResult"/>, the
/// pinned figures) into the published tax facts (ADR-0063 § 7.1 / P4b) — the same numbers the ledger entry carries, so
/// an invoice built from the event equals the ledger (<c>base + tax = gross</c>, <c>Σ tax = tax.store-{id}</c>):
/// <list type="bullet">
/// <item>inclusive: Standard at the rate, base = the charge less the contained tax;</item>
/// <item>exclusive: Standard at the rate, base = the price, tax added on top;</item>
/// <item>untaxed: Exempt (rate 0, <see cref="TaxExemptionReason.DestinationShipRule"/>) for a destination-exempt line,
/// otherwise Standard at the store's rate (0 % stores, a zero price) with no tax;</item>
/// <item>unknown rate (never projected): <see cref="TaxCategory.NotRecorded"/> / <see cref="TaxScheme.NotRecorded"/> with
/// no tax — exactly what was posted; the facts are honestly absent, never guessed.</item>
/// </list>
/// Pure.
/// </summary>
public static class SettledChargeTax
{
    /// <param name="tax">The pinned split the charge was made and posted with.</param>
    /// <param name="description">The line's description.</param>
    /// <param name="quantity">Units the line covers.</param>
    /// <param name="exempt">The line is destination-exempt (a subscription's purchase-time snapshot; usage never is).</param>
    /// <param name="taxInclusive">The regime the price was set under (used when the split itself does not say — untaxed / unknown).</param>
    /// <param name="fallbackRateBps">
    /// The rate to report when the pin predates recorded rates (<see cref="RecurringChargeTaxResult.RateBps"/> null on a
    /// taxed split): the storefront's projected rate now. Only splits pinned before P4b and settled after it take it.
    /// </param>
    public static SettledChargeTaxFacts For(
        RecurringChargeTaxResult tax, string description, long quantity, bool exempt, bool taxInclusive, int? fallbackRateBps)
    {
        var gross = tax.ChargeMinor;
        var net = tax.NetMinor;
        var inclusive = tax.Basis switch
        {
            RecurringChargeTaxBasis.Inclusive => true,
            RecurringChargeTaxBasis.Exclusive => false,
            _ => taxInclusive,
        };

        var line = tax.Basis switch
        {
            RecurringChargeTaxBasis.Unknown when exempt =>
                new SettledChargeLine(description, quantity, tax.PriceMinor, TaxCategory.Exempt, 0, gross, 0, TaxExemptionReason.DestinationShipRule),
            RecurringChargeTaxBasis.Unknown =>
                new SettledChargeLine(description, quantity, tax.PriceMinor, TaxCategory.NotRecorded, 0, gross, 0),
            RecurringChargeTaxBasis.Untaxed when exempt =>
                new SettledChargeLine(description, quantity, tax.PriceMinor, TaxCategory.Exempt, 0, gross, 0, TaxExemptionReason.DestinationShipRule),
            RecurringChargeTaxBasis.Untaxed =>
                new SettledChargeLine(description, quantity, tax.PriceMinor, TaxCategory.Standard, tax.RateBps ?? fallbackRateBps ?? 0, gross, 0),
            _ =>
                new SettledChargeLine(description, quantity, tax.PriceMinor, TaxCategory.Standard, tax.RateBps ?? fallbackRateBps ?? 0, net, tax.TaxMinor),
        };

        var scheme = line.TaxCategory == TaxCategory.NotRecorded ? TaxScheme.NotRecorded : TaxScheme.Domestic;
        return new SettledChargeTaxFacts(line, scheme, inclusive, net, tax.TaxMinor, gross);
    }
}
