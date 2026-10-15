namespace ThreeCommerce.Payments.Domain.Ledger;

/// <summary>How a recurring charge's tax was resolved — recorded so a caller can log/report it.</summary>
public enum RecurringChargeTaxBasis
{
    /// <summary>The storefront's rate is not projected yet (or the charge has no storefront): no tax posted.</summary>
    Unknown = 0,

    /// <summary>The storefront charges 0% tax, or the line is destination-exempt (ProductShipRule): no tax line.</summary>
    Untaxed = 1,

    /// <summary>Inclusive regime (AU GST / EU VAT, ADR-0038): the tax is CONTAINED in the charge and extracted.</summary>
    Inclusive = 2,

    /// <summary>
    /// Exclusive regime (US sales tax): checkout ADDS tax on top of the shelf price, but a renewal / usage charge
    /// still charges the bare price — adding tax would change what customers are charged, which is an owner
    /// decision (renewal_usage_tax_2026_10_11). Until it is made no tax is posted, exactly as before.
    /// </summary>
    ExclusiveNotCharged = 3,
}

/// <summary>The tax portion of a charge, and how it was resolved.</summary>
public readonly record struct RecurringChargeTaxResult(long TaxMinor, RecurringChargeTaxBasis Basis);

/// <summary>
/// The tax inside a subscription renewal or a metered usage charge (ADR-0063 P4) — the SAME arithmetic checkout
/// uses for the storefront (CheckoutEndpoints, ADR-0038): on an inclusive regime the charged amount is the final
/// shelf price and the contained tax is <c>amount × bps / (10000 + bps)</c>, rounded half away from zero, so a
/// renewal posts exactly the tax a checkout of the same amount in the same storefront would have posted. The
/// charged gross is never changed here. Pure and exact (decimal, never floating point).
/// </summary>
public static class RecurringChargeTax
{
    /// <param name="chargeMinor">The amount actually charged (minor units of the charge currency).</param>
    /// <param name="rateBasisPoints">The storefront's rate in force at the charge (null = not projected).</param>
    /// <param name="taxInclusive">Whether the price CONTAINS the tax (the regime the price was set under).</param>
    /// <param name="taxExempt">The line is destination-exempt (snapshotted at purchase from the ship rule).</param>
    public static RecurringChargeTaxResult For(long chargeMinor, int? rateBasisPoints, bool taxInclusive, bool taxExempt = false)
    {
        if (rateBasisPoints is null)
        {
            return new(0, RecurringChargeTaxBasis.Unknown);
        }

        if (taxExempt || rateBasisPoints <= 0 || chargeMinor <= 0)
        {
            return new(0, RecurringChargeTaxBasis.Untaxed);
        }

        if (!taxInclusive)
        {
            return new(0, RecurringChargeTaxBasis.ExclusiveNotCharged);
        }

        var bps = (decimal)rateBasisPoints.Value;
        var tax = (long)Math.Round(chargeMinor * bps / (10000m + bps), MidpointRounding.AwayFromZero);
        return new(tax, RecurringChargeTaxBasis.Inclusive);
    }

    /// <summary>
    /// The tax an exclusive regime WOULD add on top of <paramref name="priceMinor"/> (checkout's
    /// <c>amount × bps / 10000</c>, half away from zero). Not applied to renewals/usage today — kept beside the
    /// inclusive split so the exclusive-regime decision is one call-site change and is unit-pinned already.
    /// </summary>
    public static long ExclusiveTaxOn(long priceMinor, int rateBasisPoints) =>
        priceMinor <= 0 || rateBasisPoints <= 0
            ? 0
            : (long)Math.Round(priceMinor * (decimal)rateBasisPoints / 10000m, MidpointRounding.AwayFromZero);
}
