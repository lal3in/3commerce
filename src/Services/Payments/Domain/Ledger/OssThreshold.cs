namespace ThreeCommerce.Payments.Domain.Ledger;

/// <summary>
/// Payments' copy of one row of a seller's EU distance-sales running total (ADR-0063 § 16, OSS threshold crossing). Ordering
/// owns the total (<c>ordering."EuDistanceSales"</c>, written in the confirming transaction) and publishes every row
/// (<c>EuDistanceSaleRecorded</c>); this table is the projection, keyed exactly like the source — (order, destination,
/// supply) — so a redelivered row counts once and the copy's Σ equals Ordering's. Append-only. Payments reads it only to
/// re-decide a renewal's scheme (<see cref="OssThreshold"/>), never to tax a checkout.
/// </summary>
public sealed class EuDistanceSaleCopy
{
    public Guid OrderId { get; init; }
    public required string DestinationCountry { get; init; }

    /// <summary><c>""</c> for the checkout sale; the renewal's ledger reference for a settled renewal.</summary>
    public required string SourceReference { get; init; }
    public Guid TenantId { get; init; }
    public required string HomeCountry { get; init; }

    /// <summary>The UTC calendar year of the supply.</summary>
    public int Year { get; init; }
    public required string Currency { get; init; }

    /// <summary>The supply's value net of VAT, in <see cref="Currency"/> minor units.</summary>
    public long NetMinor { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}

/// <summary>
/// A seller's intra-EU distance sales net of VAT, in EUR minor units, for the PRECEDING and the CURRENT calendar year —
/// Payments' mirror of Ordering's <c>EuDistanceSalesTurnover</c>.
/// </summary>
public readonly record struct EuDistanceTurnover(long PreviousYearMinorEur, long CurrentYearMinorEur);

/// <summary>
/// The EUR 10,000 EU-wide threshold (Art. 59c of the VAT Directive) as a RENEWAL applies it (ADR-0063 § 16, OSS threshold
/// crossing). Art. 59c applies per supply, and each renewal period is a new supply (§ 7.1), so a subscription sold while
/// the seller was at or under the threshold — scheme <c>Domestic</c>, taxed at the home rate although its place of supply
/// is another Member State — is re-decided at every renewal against the seller's running total: once the preceding year
/// is strictly over EUR 10,000, or the current year plus this renewal goes over it (the crossing supply is itself OSS),
/// the renewal is an OSS supply taxed at its destination's standard rate. The same rule as checkout's
/// <c>EuOssTaxStrategy.OssApplies</c> (Ordering), including its non-EUR posture: a currency that cannot measure the EUR
/// threshold takes the destination rate (opting in below the threshold is lawful, Art. 59c(3)).
/// </summary>
public static class OssThreshold
{
    /// <summary>EUR 10,000.00 in minor units (Art. 59c(1)(c)) — Ordering's <c>EuOssTaxStrategy.ThresholdMinorEur</c>.</summary>
    public const long ThresholdMinorEur = 1_000_000;

    /// <summary>
    /// The seller's home Member State from a storefront tax jurisdiction (<c>DE</c>, <c>DE-BY</c>, <c>EL</c> → <c>GR</c>),
    /// or null when it is not an EU Member State. <c>XI</c> (Northern Ireland goods) is never a home state.
    /// </summary>
    public static string? HomeOf(string? jurisdiction)
    {
        if (string.IsNullOrWhiteSpace(jurisdiction))
        {
            return null;
        }

        var trimmed = jurisdiction.Trim();
        var dash = trimmed.IndexOf('-');
        var code = EuStandardVatRates.Normalize(dash < 0 ? trimmed : trimmed[..dash]);
        return code == "XI" ? null : code;
    }

    /// <summary>
    /// The Member State a renewal of <paramref name="destination"/> would be taxed in under OSS when the seller's home is
    /// <paramref name="homeJurisdiction"/>: the destination when both are in the EU VAT area and differ, else null (a
    /// domestic supply, a non-EU destination, or a home that is not a Member State).
    /// </summary>
    public static string? CrossBorderDestination(string? homeJurisdiction, string? destination) =>
        HomeOf(homeJurisdiction) is { } home && EuStandardVatRates.Normalize(destination) is { } area && area != home ? area : null;

    /// <summary>
    /// Whether a cross-border supply worth <paramref name="saleNetMinor"/> (net of VAT, in <paramref name="currency"/>) is
    /// over the threshold: always for a non-EUR currency; for EUR when the preceding year is STRICTLY over EUR 10,000, or
    /// the current year plus this supply is.
    /// </summary>
    public static bool Exceeded(string currency, EuDistanceTurnover turnover, long saleNetMinor) =>
        !string.Equals(currency, "EUR", StringComparison.OrdinalIgnoreCase)
        || turnover.PreviousYearMinorEur > ThresholdMinorEur
        || turnover.CurrentYearMinorEur + saleNetMinor > ThresholdMinorEur;
}
