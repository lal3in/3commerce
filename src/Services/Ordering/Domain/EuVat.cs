namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// The 27 EU Member States as ISO 3166-1 alpha-2 codes (ADR-0063 P1b), with the time zone each one's law changes
/// its rates in. Greece is <c>GR</c> here (ISO, as ship-to and storefront jurisdictions are written); its VAT prefix
/// <c>EL</c> is accepted as an alias. Special VAT territories (Canary Islands, Åland, Mount Athos, the French
/// outermost regions, Büsingen, Heligoland, Livigno, Campione, Ceuta/Melilla) and Northern Ireland (<c>XI</c>) are
/// NOT modelled: they need a postcode/region rule, which is a separate decision.
/// </summary>
public static class EuMemberStates
{
    private static readonly Dictionary<string, string> TimeZoneIds = new(StringComparer.Ordinal)
    {
        ["AT"] = "Europe/Vienna",
        ["BE"] = "Europe/Brussels",
        ["BG"] = "Europe/Sofia",
        ["HR"] = "Europe/Zagreb",
        ["CY"] = "Asia/Nicosia",
        ["CZ"] = "Europe/Prague",
        ["DK"] = "Europe/Copenhagen",
        ["EE"] = "Europe/Tallinn",
        ["FI"] = "Europe/Helsinki",
        ["FR"] = "Europe/Paris",
        ["DE"] = "Europe/Berlin",
        ["GR"] = "Europe/Athens",
        ["HU"] = "Europe/Budapest",
        ["IE"] = "Europe/Dublin",
        ["IT"] = "Europe/Rome",
        ["LV"] = "Europe/Riga",
        ["LT"] = "Europe/Vilnius",
        ["LU"] = "Europe/Luxembourg",
        ["MT"] = "Europe/Malta",
        ["NL"] = "Europe/Amsterdam",
        ["PL"] = "Europe/Warsaw",
        ["PT"] = "Europe/Lisbon",
        ["RO"] = "Europe/Bucharest",
        ["SK"] = "Europe/Bratislava",
        ["SI"] = "Europe/Ljubljana",
        ["ES"] = "Europe/Madrid",
        ["SE"] = "Europe/Stockholm",
    };

    /// <summary>The 27 Member-State codes.</summary>
    public static IReadOnlyCollection<string> Codes => TimeZoneIds.Keys;

    /// <summary>The ISO code of an EU Member State (upper-cased, <c>EL</c> → <c>GR</c>), or null when it is not one.</summary>
    public static string? Normalize(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return null;
        }

        var code = country.Trim().ToUpperInvariant();
        if (code == "EL")
        {
            code = "GR";
        }

        return TimeZoneIds.ContainsKey(code) ? code : null;
    }

    /// <summary>
    /// The Member State a storefront's <see cref="StorefrontTaxCopy.TaxJurisdiction"/> (<c>CC</c> or <c>CC-RRR</c>,
    /// ADR-0063 P2) names, or null when it is empty or outside the EU.
    /// </summary>
    public static string? OfJurisdiction(string? jurisdiction)
    {
        if (string.IsNullOrWhiteSpace(jurisdiction))
        {
            return null;
        }

        var trimmed = jurisdiction.Trim();
        var dash = trimmed.IndexOf('-');
        return Normalize(dash < 0 ? trimmed : trimmed[..dash]);
    }

    /// <summary>
    /// The calendar date in the Member State at <paramref name="instant"/> — a rate change takes effect at local
    /// midnight, so a sale at 23:30 UTC on 30 June is already a 1 July supply in Tallinn. Falls back to the UTC date
    /// only if the host has no time-zone database (the .NET runtime images ship one; a unit test pins it).
    /// </summary>
    public static DateOnly LocalDate(string memberState, DateTimeOffset instant)
    {
        var code = Normalize(memberState) ?? throw new ArgumentException($"'{memberState}' is not an EU Member State.", nameof(memberState));
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneIds[code]);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return DateOnly.FromDateTime(instant.UtcDateTime);
        }
    }

    /// <summary>Whether the host resolves every Member State's time zone (pinned by a unit test).</summary>
    public static bool AllTimeZonesResolve()
    {
        foreach (var id in TimeZoneIds.Values)
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out _))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One row of the versioned EU VAT rate table: from <see cref="EffectiveFrom"/> (a local date in that Member State)
/// until the next row of the same country and category, <see cref="RateBps"/> applies. <see cref="CategoryCode"/>
/// <c>""</c> is the standard rate; any other code is a product category with its own (reduced, super-reduced, zero)
/// rate in that country.
/// </summary>
public sealed record EuVatRate(string Country, string CategoryCode, DateOnly EffectiveFrom, int RateBps);

/// <summary>
/// The versioned EU VAT rate table (ADR-0063 P1b): rates by Member State and category, each with its effective
/// date. The baseline lives in code (<see cref="Current"/>) — a rate change is a law change and ships as a
/// reviewed, tested code change with its effective date, so a sale before the date keeps the old rate and a sale
/// on or after it gets the new one with no deploy timing involved. The table answers "what rate applies in country
/// C, to category K, on date D"; a category with no row of its own in C falls back to C's standard rate.
/// </summary>
public sealed class EuVatRateTable
{
    /// <summary>When the Union OSS scheme for intra-EU distance sales started (Directive (EU) 2017/2455).</summary>
    public static readonly DateOnly OssStart = new(2021, 7, 1);

    private readonly Dictionary<(string Country, string Category), EuVatRate[]> _rows;

    public EuVatRateTable(IEnumerable<EuVatRate> rows)
    {
        var grouped = new Dictionary<(string, string), List<EuVatRate>>();
        foreach (var row in rows)
        {
            var country = EuMemberStates.Normalize(row.Country)
                ?? throw new ArgumentException($"'{row.Country}' is not an EU Member State.", nameof(rows));
            if (row.RateBps is < 0 or > 10_000)
            {
                throw new ArgumentException($"{country} rate {row.RateBps} bps is outside 0..10000.", nameof(rows));
            }

            var key = (country, NormalizeCategory(row.CategoryCode));
            if (!grouped.TryGetValue(key, out var list))
            {
                grouped[key] = list = [];
            }

            if (list.Any(r => r.EffectiveFrom == row.EffectiveFrom))
            {
                throw new ArgumentException($"{country}/{key.Item2} has two rates effective {row.EffectiveFrom}.", nameof(rows));
            }

            list.Add(row with { Country = country, CategoryCode = key.Item2 });
        }

        _rows = grouped.ToDictionary(g => g.Key, g => g.Value.OrderBy(r => r.EffectiveFrom).ToArray());
    }

    /// <summary>
    /// The EU-27 STANDARD rates since the Union OSS started (1 July 2021), with every standard-rate change since
    /// then on its effective date. Sources: the European Commission's "VAT rates applied in the Member States" /
    /// TEDB and the national acts (FI HE 72/2024 from 1 Sep 2024; SK Act 278/2024 from 1 Jan 2025; EE from 1 Jan
    /// 2024 and 1 Jul 2025; RO Law 141/2025 from 1 Aug 2025; LU's temporary 16 % for 2023). Reduced-rate categories
    /// are deliberately NOT seeded: no product carries a VAT category yet, and an unverified reduced rate in a money
    /// path is worse than none (a category without a row is taxed at the standard rate — never under-collected).
    /// Re-verify against TEDB before EU cross-border B2C goes live and on every change of law.
    /// </summary>
    public static EuVatRateTable Current { get; } = new(
    [
        new("AT", "", OssStart, 2_000),
        new("BE", "", OssStart, 2_100),
        new("BG", "", OssStart, 2_000),
        new("HR", "", OssStart, 2_500),
        new("CY", "", OssStart, 1_900),
        new("CZ", "", OssStart, 2_100),
        new("DK", "", OssStart, 2_500),
        new("EE", "", OssStart, 2_000),
        new("EE", "", new DateOnly(2024, 1, 1), 2_200),
        new("EE", "", new DateOnly(2025, 7, 1), 2_400),
        new("FI", "", OssStart, 2_400),
        new("FI", "", new DateOnly(2024, 9, 1), 2_550),
        new("FR", "", OssStart, 2_000),
        new("DE", "", OssStart, 1_900),
        new("GR", "", OssStart, 2_400),
        new("HU", "", OssStart, 2_700),
        new("IE", "", OssStart, 2_300),
        new("IT", "", OssStart, 2_200),
        new("LV", "", OssStart, 2_100),
        new("LT", "", OssStart, 2_100),
        new("LU", "", OssStart, 1_700),
        new("LU", "", new DateOnly(2023, 1, 1), 1_600),
        new("LU", "", new DateOnly(2024, 1, 1), 1_700),
        new("MT", "", OssStart, 1_800),
        new("NL", "", OssStart, 2_100),
        new("PL", "", OssStart, 2_300),
        new("PT", "", OssStart, 2_300),
        new("RO", "", OssStart, 1_900),
        new("RO", "", new DateOnly(2025, 8, 1), 2_100),
        new("SK", "", OssStart, 2_000),
        new("SK", "", new DateOnly(2025, 1, 1), 2_300),
        new("SI", "", OssStart, 2_200),
        new("ES", "", OssStart, 2_100),
        new("SE", "", OssStart, 2_500),
    ]);

    /// <summary>The standard rate in force in <paramref name="country"/> on <paramref name="onDate"/>, or null if none.</summary>
    public int? StandardRateFor(string country, DateOnly onDate) => InForce(country, "", onDate);

    /// <summary>
    /// The rate of <paramref name="categoryCode"/> in force in <paramref name="country"/> on <paramref name="onDate"/>,
    /// or null when the country has no row of its own for that category (or the code is the standard one).
    /// </summary>
    public int? CategoryRateFor(string country, string? categoryCode, DateOnly onDate)
    {
        var category = NormalizeCategory(categoryCode);
        return category.Length == 0 ? null : InForce(country, category, onDate);
    }

    /// <summary>The rate for the category, falling back to the country's standard rate.</summary>
    public int? RateFor(string country, string? categoryCode, DateOnly onDate) =>
        CategoryRateFor(country, categoryCode, onDate) ?? StandardRateFor(country, onDate);

    /// <summary>Every row, in (country, category, date) order — for fixtures and audits.</summary>
    public IEnumerable<EuVatRate> Rows => _rows.OrderBy(r => r.Key.Country, StringComparer.Ordinal)
        .ThenBy(r => r.Key.Category, StringComparer.Ordinal)
        .SelectMany(r => r.Value);

    public static string NormalizeCategory(string? categoryCode) =>
        string.IsNullOrWhiteSpace(categoryCode) ? string.Empty : categoryCode.Trim().ToUpperInvariant();

    private int? InForce(string country, string category, DateOnly onDate)
    {
        var code = EuMemberStates.Normalize(country);
        if (code is null || !_rows.TryGetValue((code, category), out var rows))
        {
            return null;
        }

        int? rate = null;
        foreach (var row in rows)
        {
            if (row.EffectiveFrom > onDate)
            {
                break;
            }

            rate = row.RateBps;
        }

        return rate;
    }
}

/// <summary>
/// A seller's intra-EU distance sales to consumers (goods dispatched to, and B2C telecom/broadcast/electronic
/// services supplied in, another Member State), net of VAT, in EUR minor units — the measure of the EUR 10,000
/// EU-wide threshold of Art. 59c of the VAT Directive, for the PRECEDING and the CURRENT calendar year.
/// </summary>
public sealed record EuDistanceSalesTurnover(long PreviousYearMinorEur, long CurrentYearMinorEur)
{
    public static EuDistanceSalesTurnover None { get; } = new(0, 0);
}

/// <summary>
/// The EU OSS-aware tax strategy (ADR-0063 P1b, § 12 D17), selected for a storefront whose
/// <see cref="StorefrontTaxCopy.TaxJurisdiction"/> is an EU Member State — the seller's home state, which is
/// configuration (P2), never a constant here.
/// <list type="bullet">
/// <item><b>Domestic, non-EU destinations, and cross-border EU sales while the seller is at or under the EUR 10,000
/// threshold</b> (current AND preceding year, Art. 59c): the home rate — the storefront's own configured rate, so
/// these sales are byte-identical to <see cref="StorefrontRateTaxStrategy"/> — scheme <see cref="TaxScheme.Domestic"/>.</item>
/// <item><b>Cross-border EU B2C over the threshold</b> — including the very sale that takes the current year over it:
/// the DESTINATION Member State's rate in force on the supply date (its local date), from the versioned
/// <see cref="EuVatRateTable"/>, scheme <see cref="TaxScheme.Oss"/>. A non-EUR storefront cannot measure a
/// EUR threshold without an FX source (§ 12 D13), so its cross-border EU sales take the destination rate — the
/// direction that never under-collects (opting into OSS below the threshold is lawful, Art. 59c(3)).</item>
/// </list>
/// The regime is the storefront's: on an inclusive storefront the destination VAT is EXTRACTED from the shelf price
/// and the shopper pays exactly the listed amount (ADR-0038); on an exclusive one it is added. Exempt lines (a ship
/// rule's <c>ChargeDestinationTax = false</c>, ADR-0050) stay exempt.
/// <para>
/// Per-line categories: a line with a <see cref="TaxLineInput.TaxCategoryCode"/> takes that category's rate in the
/// taxing country when the table has one (else the standard rate; at home, the storefront's rate). Lines at one rate
/// are taxed exactly as <see cref="StorefrontRateTaxStrategy"/> does (one rounding of the whole base). A cart that
/// mixes rates is taxed per rate group (one rounding per group), with the shipping charge following the
/// HIGHEST-rated taxable goods — a conservative interim rule (never under-collected) until a split shipping line can
/// be persisted for pro-rata apportionment.
/// </para>
/// </summary>
public sealed class EuOssTaxStrategy : ITaxStrategy
{
    /// <summary>EUR 10,000.00 in minor units — the EU-wide distance-sales threshold (Art. 59c(1)(c)).</summary>
    public const long ThresholdMinorEur = 1_000_000;

    private readonly string _home;
    private readonly int _homeRateBps;
    private readonly bool _taxInclusive;
    private readonly EuVatRateTable _rates;
    private readonly EuDistanceSalesTurnover _turnover;
    private readonly DateTimeOffset _supplyTime;

    public EuOssTaxStrategy(
        string homeMemberState,
        int homeRateBps,
        bool taxInclusive,
        EuVatRateTable rates,
        EuDistanceSalesTurnover turnover,
        DateTimeOffset supplyTime)
    {
        _home = EuMemberStates.Normalize(homeMemberState)
            ?? throw new ArgumentException($"'{homeMemberState}' is not an EU Member State.", nameof(homeMemberState));
        _homeRateBps = homeRateBps;
        _taxInclusive = taxInclusive;
        _rates = rates;
        _turnover = turnover;
        _supplyTime = supplyTime;
    }

    public string HomeMemberState => _home;

    public TaxBreakdown TaxFor(TaxCalculationInput input)
    {
        var homeDate = EuMemberStates.LocalDate(_home, _supplyTime);
        var domestic = TaxAtRates(
            input,
            line => _rates.CategoryRateFor(_home, line.TaxCategoryCode, homeDate) ?? _homeRateBps,
            _homeRateBps,
            TaxScheme.Domestic);

        var destination = EuMemberStates.Normalize(input.DestinationCountry ?? input.ShipCountry);
        if (destination is null || destination == _home || !OssApplies(input.Currency, domestic))
        {
            return domestic;
        }

        var destinationDate = EuMemberStates.LocalDate(destination, _supplyTime);
        var standard = _rates.StandardRateFor(destination, destinationDate)
            ?? throw new InvalidOperationException($"No EU VAT rate for {destination} on {destinationDate:yyyy-MM-dd}.");
        return TaxAtRates(
            input,
            line => _rates.CategoryRateFor(destination, line.TaxCategoryCode, destinationDate) ?? standard,
            standard,
            TaxScheme.Oss);
    }

    /// <summary>
    /// A sale's value for the threshold: Σ of every line's and the shipping line's base, net of VAT (Art. 59c counts
    /// the value "exclusive of VAT"). Exempt components count at their discounted amount.
    /// </summary>
    public static long DistanceSaleValue(TaxBreakdown breakdown) =>
        breakdown.Lines.Sum(l => l.TaxableBaseMinor) + breakdown.Shipping.TaxableBaseMinor;

    private bool OssApplies(string currency, TaxBreakdown domestic)
    {
        if (!string.Equals(currency, "EUR", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _turnover.PreviousYearMinorEur > ThresholdMinorEur
            || _turnover.CurrentYearMinorEur + DistanceSaleValue(domestic) > ThresholdMinorEur;
    }

    private TaxBreakdown TaxAtRates(TaxCalculationInput input, Func<TaxLineInput, int> rateOf, int defaultRateBps, TaxScheme scheme)
    {
        var lines = input.Lines;
        var rates = new int[lines.Count];
        var distinct = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a))); // highest first
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Exemption == TaxExemptionReason.None)
            {
                rates[i] = rateOf(lines[i]);
                distinct.Add(rates[i]);
            }
        }

        if (distinct.Count <= 1)
        {
            var rate = distinct.Count == 1 ? distinct.First() : defaultRateBps;
            return new StorefrontRateTaxStrategy(rate, _taxInclusive).TaxFor(input) with { Scheme = scheme };
        }

        // Mixed rates: one computation per rate group, the other groups' lines masked as exempt (their facts are
        // taken from their own group). Shipping rides the highest-rated group that has a positive goods value.
        var shippingRate = distinct.FirstOrDefault(r => Enumerable.Range(0, lines.Count)
            .Any(i => lines[i].Exemption == TaxExemptionReason.None && rates[i] == r && lines[i].AmountMinor > 0), distinct.First());
        var facts = new LineTax[lines.Count];
        LineTax? shipping = null;
        foreach (var rate in distinct)
        {
            var masked = lines
                .Select((l, i) => l.Exemption == TaxExemptionReason.None && rates[i] != rate
                    ? l with { Exemption = TaxExemptionReason.DestinationShipRule }
                    : l)
                .ToList();
            var group = new StorefrontRateTaxStrategy(rate, _taxInclusive).TaxFor(
                input with { Lines = masked, ShippingMinor = rate == shippingRate ? input.ShippingMinor : 0 });
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Exemption != TaxExemptionReason.None || rates[i] == rate)
                {
                    facts[i] = group.Lines[i];
                }
            }

            if (rate == shippingRate)
            {
                shipping = group.Shipping;
            }
        }

        return new TaxBreakdown(facts, shipping!, scheme, _taxInclusive);
    }
}

/// <summary>
/// Picks THE tax strategy for a storefront (ADR-0063 § 7.1/7.3): the EU OSS-aware strategy when its
/// <see cref="StorefrontTaxCopy.TaxJurisdiction"/> is an EU Member State (P2 configuration), otherwise its single
/// configured rate (<see cref="StorefrontRateTaxStrategy"/> — every non-EU and unconfigured storefront is unchanged).
/// </summary>
public static class TaxStrategySelector
{
    /// <summary>The storefront's home EU Member State, or null when it is not taxed under EU rules.</summary>
    public static string? EuHomeMemberState(StorefrontTaxCopy? storefront) =>
        storefront is null ? null : EuMemberStates.OfJurisdiction(storefront.TaxJurisdiction);

    /// <summary>
    /// Whether the threshold turnover matters for this sale: an EU storefront selling to ANOTHER Member State. Only
    /// then does checkout need to read the seller's running total.
    /// </summary>
    public static bool NeedsDistanceSalesTurnover(StorefrontTaxCopy? storefront, string? destinationCountry) =>
        EuHomeMemberState(storefront) is { } home
        && EuMemberStates.Normalize(destinationCountry) is { } destination
        && destination != home;

    public static ITaxStrategy For(
        StorefrontTaxCopy? storefront,
        EuDistanceSalesTurnover turnover,
        DateTimeOffset supplyTime,
        EuVatRateTable? rates = null) =>
        EuHomeMemberState(storefront) is { } home
            ? new EuOssTaxStrategy(home, storefront!.TaxRateBasisPoints, storefront.TaxInclusive, rates ?? EuVatRateTable.Current, turnover, supplyTime)
            : StorefrontRateTaxStrategy.For(storefront);
}
