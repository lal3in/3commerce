namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// The part of a sale's tax charged under the Union OSS scheme, by Member State of consumption (ADR-0063 § 16,
/// per-destination OSS tax accounts) — what Payments posts to one liability account per destination, because an OSS
/// return is filed per Member State. A component is OSS when the sale's scheme is <see cref="TaxScheme.Oss"/> and the
/// component was supplied into an EU VAT area destination (a Member State, or <c>XI</c> for Northern Ireland goods) other
/// than the storefront's home one: exactly the components <see cref="EuOssTaxStrategy"/> taxed at their destination's
/// rate (with the scheme Oss, every cross-border component is over the threshold). Each line at its own destination, the
/// shipping at the sale's. Home-country components, exports (zero-rated) and every component of a non-OSS sale contribute
/// nothing, so a sale with no OSS part reports none and posts exactly as before.
/// </summary>
public static class OssTaxShares
{
    /// <summary>
    /// (destination, tax) per Member State, ordinal by country, positive amounts only; empty for a non-OSS sale. Σ never
    /// exceeds <see cref="TaxBreakdown.TaxMinor"/>.
    /// </summary>
    public static IReadOnlyList<(string Country, long TaxMinor)> Of(TaxBreakdown tax, string? jurisdiction, string cartDestination)
    {
        if (tax.Scheme != TaxScheme.Oss || EuMemberStates.OfJurisdiction(jurisdiction) is not { } home)
        {
            return [];
        }

        var saleDestination = tax.SaleDestination ?? cartDestination;
        var shares = new SortedDictionary<string, long>(StringComparer.Ordinal);
        void Add(string? destination, long taxMinor)
        {
            if (taxMinor > 0 && EuVatArea.Normalize(destination) is { } area && area != home)
            {
                shares[area] = shares.GetValueOrDefault(area) + taxMinor;
            }
        }

        for (var i = 0; i < tax.Lines.Count; i++)
        {
            Add(tax.LineDestinations?[i] ?? saleDestination, tax.Lines[i].TaxMinor);
        }

        Add(saleDestination, tax.Shipping.TaxMinor);
        return shares.Select(s => (s.Key, s.Value)).ToList();
    }
}
