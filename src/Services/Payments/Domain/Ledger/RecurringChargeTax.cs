namespace ThreeCommerce.Payments.Domain.Ledger;

/// <summary>How a recurring charge's tax was resolved — recorded so a caller can log/report it.</summary>
public enum RecurringChargeTaxBasis
{
    /// <summary>
    /// The storefront's rate is not projected yet (or the charge has no storefront): the bare price is charged and no
    /// tax is posted — never a guessed rate. On an exclusive store this UNDER-COLLECTS the tax, so callers log it
    /// loudly; it heals as soon as the store's config is (re)published (renewal_usage_tax_exclusive_decision).
    /// </summary>
    Unknown = 0,

    /// <summary>The storefront charges 0% tax, or the line is destination-exempt (ProductShipRule): no tax line.</summary>
    Untaxed = 1,

    /// <summary>Inclusive regime (AU GST / EU VAT, ADR-0038): the tax is CONTAINED in the charge and extracted.</summary>
    Inclusive = 2,

    /// <summary>
    /// Exclusive regime (US sales tax, ADR-0038): the tax is ADDED on top of the shelf price, exactly as checkout
    /// adds it to the first period — owner decision 2026-10-15, option A (renewal_usage_tax_exclusive_decision).
    /// </summary>
    Exclusive = 3,
}

/// <summary>
/// The tax of one renewal / usage charge: the shelf <see cref="PriceMinor"/> it was computed on, the
/// <see cref="TaxMinor"/> portion, and how it was resolved. <see cref="ChargeMinor"/> is what the customer is
/// charged (price + tax on an exclusive store, the price itself otherwise); <see cref="NetMinor"/> is the revenue.
/// </summary>
public readonly record struct RecurringChargeTaxResult(long PriceMinor, long TaxMinor, RecurringChargeTaxBasis Basis)
{
    /// <summary>The gross the provider is asked to charge: the price, plus the tax only when it is added on top.</summary>
    public long ChargeMinor => Basis == RecurringChargeTaxBasis.Exclusive ? PriceMinor + TaxMinor : PriceMinor;

    /// <summary>Net revenue: the charge less its tax (the price itself on an exclusive store).</summary>
    public long NetMinor => ChargeMinor - TaxMinor;
}

/// <summary>
/// The tax of a subscription renewal or a metered usage charge (ADR-0063 P4) — the SAME arithmetic checkout uses
/// for the storefront (CheckoutEndpoints, ADR-0038), so a renewal charges and posts exactly what a checkout of the
/// same amount in the same storefront would have:
/// <list type="bullet">
/// <item>inclusive (AU GST / EU VAT): the price is the final shelf price; the contained tax is
/// <c>price × bps / (10000 + bps)</c> and the charge is the price, unchanged;</item>
/// <item>exclusive (US sales tax): the tax <c>price × bps / 10000</c> is ADDED and the charge is price + tax
/// (owner decision 2026-10-15, option A — before it a renewal charged the bare price and the merchant owed the
/// uncollected tax).</item>
/// </list>
/// Both round half away from zero. Pure and exact (decimal, never floating point).
/// </summary>
public static class RecurringChargeTax
{
    /// <param name="priceMinor">The shelf price of the period / usage (minor units of the charge currency).</param>
    /// <param name="rateBasisPoints">The storefront's rate in force at the charge (null = not projected).</param>
    /// <param name="taxInclusive">Whether the price CONTAINS the tax (the regime the price was set under).</param>
    /// <param name="taxExempt">The line is destination-exempt (snapshotted at purchase from the ship rule).</param>
    public static RecurringChargeTaxResult For(long priceMinor, int? rateBasisPoints, bool taxInclusive, bool taxExempt = false)
    {
        if (rateBasisPoints is null)
        {
            return new(priceMinor, 0, RecurringChargeTaxBasis.Unknown);
        }

        if (taxExempt || rateBasisPoints <= 0 || priceMinor <= 0)
        {
            return new(priceMinor, 0, RecurringChargeTaxBasis.Untaxed);
        }

        if (!taxInclusive)
        {
            return new(priceMinor, ExclusiveTaxOn(priceMinor, rateBasisPoints.Value), RecurringChargeTaxBasis.Exclusive);
        }

        var bps = (decimal)rateBasisPoints.Value;
        var tax = (long)Math.Round(priceMinor * bps / (10000m + bps), MidpointRounding.AwayFromZero);
        return new(priceMinor, tax, RecurringChargeTaxBasis.Inclusive);
    }

    /// <summary>
    /// The tax an exclusive regime adds on top of <paramref name="priceMinor"/> — checkout's
    /// <c>amount × bps / 10000</c>, half away from zero (unit-swept equal to CheckoutEndpoints).
    /// </summary>
    public static long ExclusiveTaxOn(long priceMinor, int rateBasisPoints) =>
        priceMinor <= 0 || rateBasisPoints <= 0
            ? 0
            : (long)Math.Round(priceMinor * (decimal)rateBasisPoints / 10000m, MidpointRounding.AwayFromZero);
}
