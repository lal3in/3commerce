namespace ThreeCommerce.Payments.Domain.Ledger;

/// <summary>
/// The part of a charge's tax that was charged under the Union OSS scheme, split by the Member State of consumption
/// (ADR-0063 § 16, per-destination OSS tax accounts). An OSS return is filed per destination, so each share is posted to
/// that destination's own liability account (<see cref="Accounts.OssTaxFor"/>) instead of the store's single tax account,
/// and every reversal (refund, chargeback) takes the tax back from the same accounts. Whatever tax is NOT in the split
/// — the home-rate (domestic) part of a sale — stays on the store's tax account exactly as before, so a charge with no
/// OSS part (every non-EU store, every domestic sale) is posted byte-identically.
/// <para>
/// Persisted on <see cref="Payment.OssTax"/> as <c>FR:200,IT:150</c> (ISO alpha-2 upper-case, <c>XI</c> allowed;
/// destinations in ordinal order; one entry per country; only positive amounts).
/// </para>
/// </summary>
public sealed class OssTaxSplit
{
    public static OssTaxSplit None { get; } = new([]);

    private OssTaxSplit(IReadOnlyList<(string Country, long TaxMinor)> shares) => Shares = shares;

    /// <summary>The destinations and their OSS tax, ordinal by country.</summary>
    public IReadOnlyList<(string Country, long TaxMinor)> Shares { get; }

    /// <summary>Σ of the shares.</summary>
    public long TotalMinor => Shares.Sum(s => s.TaxMinor);

    public bool IsEmpty => Shares.Count == 0;

    /// <summary>
    /// A split from (country, tax) pairs: countries trimmed and upper-cased, the same country summed, zero and negative
    /// amounts and blank countries dropped.
    /// </summary>
    public static OssTaxSplit Of(IEnumerable<(string? Country, long TaxMinor)>? shares)
    {
        if (shares is null)
        {
            return None;
        }

        var merged = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (country, tax) in shares)
        {
            if (string.IsNullOrWhiteSpace(country) || tax <= 0)
            {
                continue;
            }

            var code = country.Trim().ToUpperInvariant();
            merged[code] = merged.GetValueOrDefault(code) + tax;
        }

        return merged.Count == 0 ? None : new OssTaxSplit(merged.Select(m => (m.Key, m.Value)).ToList());
    }

    /// <summary>The whole <paramref name="taxMinor"/> charged in one destination (an OSS renewal).</summary>
    public static OssTaxSplit Single(string country, long taxMinor) => Of([(country, taxMinor)]);

    /// <summary>The persisted form (<c>FR:200,IT:150</c>), or null when there is no OSS part.</summary>
    public string? Serialize() =>
        IsEmpty ? null : string.Join(',', Shares.Select(s => FormattableString.Invariant($"{s.Country}:{s.TaxMinor}")));

    /// <summary>Reads the persisted form; null, blank or malformed entries read as no OSS part for that entry.</summary>
    public static OssTaxSplit Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return None;
        }

        var pairs = new List<(string? Country, long TaxMinor)>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            if (colon > 0 && long.TryParse(part[(colon + 1)..], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var minor))
            {
                pairs.Add((part[..colon], minor));
            }
        }

        return Of(pairs);
    }

    /// <summary>
    /// The split that fits inside a charge whose whole tax is <paramref name="taxMinor"/>: itself when Σ ≤ the tax, else
    /// none — a split that does not fit is never posted (the store's account then takes the whole tax, the pre-split
    /// behaviour), so an entry can never be unbalanced or carry a negative domestic part.
    /// </summary>
    public OssTaxSplit Within(long taxMinor) => TotalMinor <= taxMinor ? this : None;

    /// <summary>
    /// The (account, amount) tax lines of a charge whose whole tax is <paramref name="taxMinor"/>: each OSS share to its
    /// destination's account, the rest to <paramref name="taxAccount"/> (the store's). Zero amounts are left out
    /// (<c>ck_line_one_side</c>); Σ of the lines is <paramref name="taxMinor"/>.
    /// </summary>
    public IReadOnlyList<(string Account, long Minor)> Lines(string taxAccount, long taxMinor)
    {
        var fitting = Within(taxMinor);
        var lines = new List<(string Account, long Minor)>();
        var domestic = taxMinor - fitting.TotalMinor;
        if (domestic > 0)
        {
            lines.Add((taxAccount, domestic));
        }

        lines.AddRange(fitting.Shares.Select(s => (Accounts.OssTaxFor(taxAccount, s.Country), s.TaxMinor)));
        return lines;
    }

    /// <summary>
    /// The share of a partial reversal (a refund or chargeback of part of the charge) that comes back from each OSS
    /// account: <paramref name="reversedTaxMinor"/> of the charge's <paramref name="bookedTaxMinor"/> is allocated across
    /// the booked buckets — the domestic remainder and each destination — in proportion to what each was booked, by
    /// largest remainder (ties to the earlier bucket, the domestic one first), so the parts sum exactly to the reversal and
    /// a full reversal returns each bucket exactly. The result is the OSS part of the reversal; the rest is domestic.
    /// <para>
    /// <paramref name="alreadyReversed"/> is what earlier reversals of the same charge took back from each OSS account: no
    /// destination is ever reversed past what it was booked, and the <paramref name="final"/> reversal (the one that leaves
    /// nothing of the charge standing) returns each destination's exact remainder, so per-reversal rounding can never
    /// leave a minor unit on an OSS account. Whatever the OSS part does not take is the domestic part.
    /// </para>
    /// </summary>
    public OssTaxSplit Reversal(long reversedTaxMinor, long bookedTaxMinor, OssTaxSplit? alreadyReversed = null, bool final = false)
    {
        var fitting = Within(bookedTaxMinor);
        if (fitting.IsEmpty || reversedTaxMinor <= 0 || bookedTaxMinor <= 0)
        {
            return None;
        }

        var before = alreadyReversed ?? None;
        var remaining = Of(fitting.Shares.Select(s => ((string?)s.Country, s.TaxMinor - before.MinorFor(s.Country))));
        if ((final || reversedTaxMinor >= bookedTaxMinor) && remaining.TotalMinor <= reversedTaxMinor)
        {
            return remaining;
        }

        var proportional = Proportional(fitting, reversedTaxMinor, bookedTaxMinor);
        return Of(proportional.Shares.Select(s => ((string?)s.Country, Math.Min(s.TaxMinor, remaining.MinorFor(s.Country)))));
    }

    /// <summary>The OSS tax of <paramref name="country"/> in this split (0 when it has none).</summary>
    public long MinorFor(string country) =>
        Shares.Where(s => string.Equals(s.Country, country, StringComparison.OrdinalIgnoreCase)).Sum(s => s.TaxMinor);

    private static OssTaxSplit Proportional(OssTaxSplit fitting, long reversedTaxMinor, long bookedTaxMinor)
    {
        if (reversedTaxMinor >= bookedTaxMinor)
        {
            return fitting;
        }

        var buckets = new List<long> { bookedTaxMinor - fitting.TotalMinor };
        buckets.AddRange(fitting.Shares.Select(s => s.TaxMinor));
        var allocated = new long[buckets.Count];
        var remainders = new decimal[buckets.Count];
        for (var i = 0; i < buckets.Count; i++)
        {
            var exact = (decimal)reversedTaxMinor * buckets[i] / bookedTaxMinor;
            allocated[i] = (long)Math.Floor(exact);
            remainders[i] = exact - allocated[i];
        }

        var left = reversedTaxMinor - allocated.Sum();
        foreach (var i in Enumerable.Range(0, buckets.Count).OrderByDescending(i => remainders[i]).ThenBy(i => i).Take((int)left))
        {
            allocated[i]++;
        }

        return Of(fitting.Shares.Select((s, index) => ((string?)s.Country, allocated[index + 1])));
    }
}
