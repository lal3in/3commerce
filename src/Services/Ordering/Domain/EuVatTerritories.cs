namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// A territory of an EU Member State that is OUTSIDE the EU VAT area (Directive 2006/112/EC Art. 6): a supply into it
/// is an export from the EU VAT area — zero-rated, <see cref="TaxExemptionReason.OutsideEuVatArea"/> — even though
/// the address is in a Member State (ADR-0063 § 16, special territories).
/// </summary>
/// <param name="Code">A stable key for the territory (tests, audits).</param>
/// <param name="Name">Its name, as the Directive gives it.</param>
/// <param name="MemberState">The Member State it belongs to (ISO 3166-1).</param>
/// <param name="LegalBasis">The Directive provision that excludes it.</param>
/// <param name="CountryCodes">
/// ISO 3166-1 codes an address may carry for it instead of the Member State's (<c>IC</c>/<c>EA</c> are ISO
/// "exceptionally reserved" codes; <c>AX</c>, <c>GP</c>… are assigned codes). A match on one always applies.
/// </param>
/// <param name="Subdivisions">ISO 3166-2 subdivision codes that are wholly the territory. A match always applies.</param>
/// <param name="PostcodePrefixes">Postcode prefixes (digits, after normalization) that are wholly the territory.</param>
/// <param name="PostcodesVerified">
/// Whether those prefixes are verified. An UNVERIFIED prefix is recognised but NOT applied: the address is taxed as
/// the Member State until the prefix is verified — over-collecting until then, never under-collecting. Every
/// unverified prefix is listed on the launch-gate tracker row <c>eu_vat_rate_table_tedb_verification</c>.
/// </param>
/// <param name="Note">Where the identifiers come from, or what has to be verified.</param>
public sealed record EuSpecialTerritory(
    string Code,
    string Name,
    string MemberState,
    string LegalBasis,
    IReadOnlyList<string> CountryCodes,
    IReadOnlyList<string> Subdivisions,
    IReadOnlyList<string> PostcodePrefixes,
    bool PostcodesVerified,
    string Note);

/// <summary>How an address matched a special territory, and whether that match is applied.</summary>
public sealed record EuSpecialTerritoryMatch(EuSpecialTerritory Territory, string MatchedBy, bool Applies);

/// <summary>
/// Where a component of a sale is supplied for VAT: the <see cref="Destination"/> country whose rules apply — an ISO
/// 3166-1 code, or <c>XI</c> for goods supplied into Northern Ireland — and, when the address lies in a territory
/// outside the EU VAT area, that territory (the supply is then an export, zero-rated).
/// </summary>
public sealed record TaxPlace(string Destination, EuSpecialTerritory? OutsideEuVatArea = null)
{
    public bool IsOutsideEuVatArea => OutsideEuVatArea is not null;
}

/// <summary>
/// The territorial rules of the EU VAT area that a country code alone cannot answer (ADR-0063 § 16), all keyed off the
/// address — the country, its ISO 3166-2 region and its postcode:
/// <list type="bullet">
/// <item><b>Special territories</b> (Art. 6): parts of a Member State outside the EU VAT area — the Canary Islands,
/// Ceuta, Melilla, Åland, Mount Athos, the French outermost regions (Art. 349 TFEU), Heligoland, Büsingen, Livigno,
/// Campione d'Italia. A supply into one is an export from the EU VAT area: zero-rated.</item>
/// <item><b>Northern Ireland</b> (<c>XI</c>, Windsor Framework / Protocol on Ireland/Northern Ireland Art. 8): inside
/// the EU VAT area for GOODS — a distance sale into it is taxed at the UK rate under the Union OSS and counts toward
/// the EUR 10,000 threshold — while SERVICES follow the rest of the UK (outside the EU).</item>
/// <item><b>Art. 7</b>: Monaco is treated as France (an address in <c>MC</c> is taxed as <c>FR</c>); the UK Sovereign
/// Base Areas of Akrotiri and Dhekelia as Cyprus (their addresses already carry <c>CY</c>).</item>
/// </list>
/// Detection order: the country code (an assigned or reserved code for the territory), then the ISO 3166-2
/// subdivision, then the postcode prefix. A region is accepted as <c>ES-CN</c> or <c>CN</c> (the address's country is
/// prefixed); a postcode is upper-cased with spaces and dashes removed, and a leading country prefix (<c>AX-22100</c>,
/// <c>FI-22100</c>) is dropped. Sources: Directive 2006/112/EC Art. 6 and 7 (as listed in
/// <c>docs/reference/tax-invoicing-research.md</c> § 2), ISO 3166-1/-2, and the owner's postcode list of 2026-11-06
/// (Canaries 35/38, Ceuta 51, Melilla 52, Åland 22xxx). Anything else is "to verify" and not applied.
/// </summary>
public static class EuVatTerritories
{
    /// <summary>The VAT code of Northern Ireland for goods.</summary>
    public const string NorthernIreland = "XI";

    /// <summary>Every Art. 6 territory an address can be in, in detection order.</summary>
    public static IReadOnlyList<EuSpecialTerritory> All { get; } =
    [
        new("ES-CANARIAS", "Canary Islands", "ES", "Art. 6(1)(b)", ["IC"], ["ES-CN", "ES-GC", "ES-TF"], ["35", "38"], true,
            "Postcodes 35xxx (Las Palmas) and 38xxx (Santa Cruz de Tenerife): owner decision 2026-11-06."),
        new("ES-CEUTA", "Ceuta", "ES", "Art. 6(2)(c)", ["EA"], ["ES-CE"], ["51"], true,
            "Postcode 51xxx: owner decision 2026-11-06. EA (ISO reserved) covers Ceuta and Melilla."),
        new("ES-MELILLA", "Melilla", "ES", "Art. 6(2)(d)", ["EA"], ["ES-ML"], ["52"], true,
            "Postcode 52xxx: owner decision 2026-11-06."),
        new("FI-ALAND", "Åland Islands", "FI", "Art. 6(1)(d)", ["AX"], ["FI-01"], ["22"], true,
            "Postcodes 22xxx: owner decision 2026-11-06."),
        new("GR-ATHOS", "Mount Athos", "GR", "Art. 6(1)(a)", [], ["GR-69"], ["63086"], false,
            "TO VERIFY: postcode 630 86 (Karyes) — not in the research or the Directive; the ISO 3166-2 GR-69 region applies."),
        new("FR-GUADELOUPE", "Guadeloupe", "FR", "Art. 6(1)(c), Art. 349 TFEU", ["GP"], ["FR-971"], ["971"], true,
            "French overseas departments: the postcode starts with the department number (971); 97150 Saint-Martin and 97133 Saint-Barthélemy share it and are outside the EU VAT area too."),
        new("FR-MARTINIQUE", "Martinique", "FR", "Art. 6(1)(c), Art. 349 TFEU", ["MQ"], ["FR-972"], ["972"], true,
            "French overseas department 972."),
        new("FR-GUYANE", "French Guiana", "FR", "Art. 6(1)(c), Art. 349 TFEU", ["GF"], ["FR-973"], ["973"], true,
            "French overseas department 973."),
        new("FR-REUNION", "Réunion", "FR", "Art. 6(1)(c), Art. 349 TFEU", ["RE"], ["FR-974"], ["974"], true,
            "French overseas department 974."),
        new("FR-MAYOTTE", "Mayotte", "FR", "Art. 6(1)(c), Art. 349 TFEU", ["YT"], ["FR-976"], ["976"], true,
            "French overseas department 976."),
        new("FR-SAINT-MARTIN", "Saint-Martin", "FR", "Art. 6(1)(c), Art. 349 TFEU", ["MF"], ["FR-MF"], [], true,
            "Its postcode 97150 is inside the 971 prefix."),
        new("DE-HELGOLAND", "Heligoland", "DE", "Art. 6(2)(a)", [], [], ["27498"], false,
            "TO VERIFY: postcode 27498 — not in the research or the Directive; no ISO 3166-2 subdivision exists."),
        new("DE-BUSINGEN", "Territory of Büsingen", "DE", "Art. 6(2)(b)", [], [], ["78266"], false,
            "TO VERIFY: postcode 78266 — not in the research or the Directive; no ISO 3166-2 subdivision exists."),
        new("IT-LIVIGNO", "Livigno", "IT", "Art. 6(2)(e)", [], [], ["23041"], false,
            "TO VERIFY: postcode 23041 (and whether Trepalle uses another) — not in the research or the Directive."),
        new("IT-CAMPIONE", "Campione d'Italia", "IT", "Art. 6(1)(f) (moved from Art. 6(2) by Directive (EU) 2019/475 — to verify)", [], [], ["22061"], false,
            "TO VERIFY: postcode 22061 and the Art. 6 paragraph. The Italian waters of Lake Lugano carry no addresses."),
    ];

    /// <summary>
    /// The place of supply for VAT of a component supplied at this address: <paramref name="goods"/> distinguishes a
    /// physical good (Northern Ireland is in the EU VAT area for goods) from a service (it follows the UK).
    /// </summary>
    public static TaxPlace Place(string? country, string? region, string? postcode, bool goods)
    {
        var code = Code(country);
        if (code == "MC")
        {
            return new TaxPlace("FR"); // Art. 7(1): Monaco is not a third territory — it is taxed as France.
        }

        if (IsNorthernIreland(code, region, postcode))
        {
            return new TaxPlace(goods ? NorthernIreland : "GB");
        }

        return Match(code, region, postcode) is { Applies: true } match
            ? new TaxPlace(match.Territory.MemberState, match.Territory)
            : new TaxPlace(code);
    }

    /// <summary>
    /// The special territory this address is in, how it matched, and whether the match is applied (a country code or
    /// a subdivision always is; a postcode only when its prefixes are verified) — or null when it is in none.
    /// </summary>
    public static EuSpecialTerritoryMatch? Match(string? country, string? region, string? postcode)
    {
        var code = Code(country);
        if (code.Length == 0)
        {
            return null;
        }

        EuSpecialTerritoryMatch? pending = null;
        foreach (var territory in All)
        {
            if (territory.CountryCodes.Contains(code))
            {
                return new EuSpecialTerritoryMatch(territory, $"country {code}", true);
            }

            if (territory.MemberState != code)
            {
                continue;
            }

            if (Subdivision(code, region) is { } subdivision && territory.Subdivisions.Contains(subdivision))
            {
                return new EuSpecialTerritoryMatch(territory, $"region {subdivision}", true);
            }

            if (Postcode(code, postcode) is { } digits && territory.PostcodePrefixes.FirstOrDefault(digits.StartsWith) is { } prefix)
            {
                var match = new EuSpecialTerritoryMatch(territory, $"postcode {prefix}", territory.PostcodesVerified);
                if (match.Applies)
                {
                    return match;
                }

                pending ??= match;
            }
        }

        return pending;
    }

    /// <summary>
    /// Whether the address is in Northern Ireland: the code <c>XI</c>, or a UK address in ISO 3166-2 <c>GB-NIR</c> or
    /// with a <c>BT</c> postcode (the Belfast postcode area, which is all of Northern Ireland).
    /// </summary>
    public static bool IsNorthernIreland(string? country, string? region, string? postcode)
    {
        var code = Code(country);
        if (code == NorthernIreland)
        {
            return true;
        }

        if (code != "GB")
        {
            return false;
        }

        if (Subdivision(code, region) == "GB-NIR")
        {
            return true;
        }

        var compact = Compact(postcode);
        return compact.Length > 2 && compact.StartsWith("BT", StringComparison.Ordinal) && char.IsAsciiDigit(compact[2]);
    }

    private static string Code(string? country) =>
        string.IsNullOrWhiteSpace(country) ? string.Empty : country.Trim().ToUpperInvariant();

    private static string? Subdivision(string code, string? region)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return null;
        }

        var value = region.Trim().ToUpperInvariant();
        return value.Contains('-') ? value : $"{code}-{value}";
    }

    /// <summary>The postcode's digits for prefix matching (a leading country prefix dropped), or null if not numeric.</summary>
    private static string? Postcode(string code, string? postcode)
    {
        var compact = Compact(postcode);
        if (compact.Length > 2 && char.IsAsciiLetter(compact[0]) && char.IsAsciiLetter(compact[1]) && char.IsAsciiDigit(compact[2]))
        {
            var prefix = compact[..2];
            if (prefix == code || All.Any(t => t.MemberState == code && t.CountryCodes.Contains(prefix)))
            {
                compact = compact[2..];
            }
        }

        return compact.Length > 0 && compact.All(char.IsAsciiDigit) ? compact : null;
    }

    private static string Compact(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Concat(value.Where(c => !char.IsWhiteSpace(c) && c != '-')).ToUpperInvariant();
}
