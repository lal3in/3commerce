namespace ThreeCommerce.Payments.Domain.Ledger;

/// <summary>
/// The EU-27 STANDARD VAT rates by Member State and local effective date — what an OSS subscription renews at
/// (ADR-0063 § 16, OSS renewals, owner decision 2026-11-06): its DESTINATION's standard rate in force on the charge's
/// local date, not the storefront's.
/// <para>
/// A deliberate COPY of Ordering's <c>EuVatRateTable.Current</c> (standard rows only) and <c>EuMemberStates.LocalDate</c>:
/// Payments cannot reference Ordering's domain and BuildingBlocks holds no domain logic (AGENTS.md), and a rate is a law
/// fact both services must apply identically. <c>EuStandardVatRateParityTests</c> (the integration project's unit lane,
/// the one project that sees both) compares every country's rate on every day from the OSS start to the last row + 1
/// year and the local dates at the midnight edges, so a rate added to one table and not the other fails the build. Add a
/// change of law to BOTH, with its effective date (the TEDB launch gate, <c>eu_vat_rate_table_tedb_verification</c>,
/// covers both copies).
/// </para>
/// </summary>
public static class EuStandardVatRates
{
    /// <summary>When the Union OSS scheme started (Directive (EU) 2017/2455) — the first row of every country.</summary>
    public static readonly DateOnly OssStart = new(2021, 7, 1);

    private static readonly Dictionary<string, (string TimeZone, (DateOnly From, int Bps)[] Rates)> Countries = new(StringComparer.Ordinal)
    {
        ["AT"] = ("Europe/Vienna", [(OssStart, 2_000)]),
        ["BE"] = ("Europe/Brussels", [(OssStart, 2_100)]),
        ["BG"] = ("Europe/Sofia", [(OssStart, 2_000)]),
        ["HR"] = ("Europe/Zagreb", [(OssStart, 2_500)]),
        ["CY"] = ("Asia/Nicosia", [(OssStart, 1_900)]),
        ["CZ"] = ("Europe/Prague", [(OssStart, 2_100)]),
        ["DK"] = ("Europe/Copenhagen", [(OssStart, 2_500)]),
        ["EE"] = ("Europe/Tallinn", [(OssStart, 2_000), (new DateOnly(2024, 1, 1), 2_200), (new DateOnly(2025, 7, 1), 2_400)]),
        ["FI"] = ("Europe/Helsinki", [(OssStart, 2_400), (new DateOnly(2024, 9, 1), 2_550)]),
        ["FR"] = ("Europe/Paris", [(OssStart, 2_000)]),
        ["DE"] = ("Europe/Berlin", [(OssStart, 1_900)]),
        ["GR"] = ("Europe/Athens", [(OssStart, 2_400)]),
        ["HU"] = ("Europe/Budapest", [(OssStart, 2_700)]),
        ["IE"] = ("Europe/Dublin", [(OssStart, 2_300)]),
        ["IT"] = ("Europe/Rome", [(OssStart, 2_200)]),
        ["LV"] = ("Europe/Riga", [(OssStart, 2_100)]),
        ["LT"] = ("Europe/Vilnius", [(OssStart, 2_100)]),
        ["LU"] = ("Europe/Luxembourg", [(OssStart, 1_700), (new DateOnly(2023, 1, 1), 1_600), (new DateOnly(2024, 1, 1), 1_700)]),
        ["MT"] = ("Europe/Malta", [(OssStart, 1_800)]),
        ["NL"] = ("Europe/Amsterdam", [(OssStart, 2_100)]),
        ["PL"] = ("Europe/Warsaw", [(OssStart, 2_300)]),
        ["PT"] = ("Europe/Lisbon", [(OssStart, 2_300)]),
        ["RO"] = ("Europe/Bucharest", [(OssStart, 1_900), (new DateOnly(2025, 8, 1), 2_100)]),
        ["SK"] = ("Europe/Bratislava", [(OssStart, 2_000), (new DateOnly(2025, 1, 1), 2_300)]),
        ["SI"] = ("Europe/Ljubljana", [(OssStart, 2_200)]),
        ["ES"] = ("Europe/Madrid", [(OssStart, 2_100)]),
        ["SE"] = ("Europe/Stockholm", [(OssStart, 2_500)]),
    };

    /// <summary>The 27 Member-State codes (ISO alpha-2; Greece is <c>GR</c>).</summary>
    public static IReadOnlyCollection<string> Codes => Countries.Keys;

    /// <summary>The ISO code of an EU Member State (upper-cased, <c>EL</c> → <c>GR</c>), or null when it is not one.</summary>
    public static string? Normalize(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return null;
        }

        var code = country.Trim().ToUpperInvariant();
        code = code == "EL" ? "GR" : code;
        return Countries.ContainsKey(code) ? code : null;
    }

    /// <summary>The date in the Member State at <paramref name="instant"/> (a rate changes at LOCAL midnight); the UTC date
    /// only if the host has no time-zone database.</summary>
    public static DateOnly LocalDate(string memberState, DateTimeOffset instant)
    {
        var code = Normalize(memberState) ?? throw new ArgumentException($"'{memberState}' is not an EU Member State.", nameof(memberState));
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(Countries[code].TimeZone);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return DateOnly.FromDateTime(instant.UtcDateTime);
        }
    }

    /// <summary>The standard rate in force in <paramref name="country"/> on <paramref name="onDate"/>, or null (not a
    /// Member State, or before its first row).</summary>
    public static int? StandardRateFor(string country, DateOnly onDate)
    {
        if (Normalize(country) is not { } code)
        {
            return null;
        }

        int? rate = null;
        foreach (var (from, bps) in Countries[code].Rates)
        {
            if (from > onDate)
            {
                break;
            }

            rate = bps;
        }

        return rate;
    }

    /// <summary>The standard rate in force in <paramref name="country"/> at <paramref name="instant"/> (its local date).</summary>
    public static int? StandardRateAt(string country, DateTimeOffset instant) =>
        Normalize(country) is { } code ? StandardRateFor(code, LocalDate(code, instant)) : null;

    /// <summary>Every (country, effective-from, bps) row, for the parity test.</summary>
    public static IEnumerable<(string Country, DateOnly From, int Bps)> Rows =>
        Countries.OrderBy(c => c.Key, StringComparer.Ordinal).SelectMany(c => c.Value.Rates.Select(r => (c.Key, r.From, r.Bps)));
}
