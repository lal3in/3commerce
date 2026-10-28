using System.Text.RegularExpressions;
using ThreeCommerce.BuildingBlocks.Contracts.Tax;

namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// The buyer's billing address (ADR-0063 P3, Art. 226(5)): where the invoice is addressed. Same shape as the
/// ship-to snapshot on <see cref="CheckoutAttempt"/>/<see cref="Order"/>. When the shopper gives none, it IS the
/// ship-to address (<see cref="BuyerIdentity.ResolveBillTo"/>).
/// </summary>
public sealed record BillToAddress(string Name, string Line1, string City, string? Region, string Postcode, string Country);

/// <summary>
/// The optional business-purchase block (ADR-0063 P3, § 8): the buying company's name and, when it has one, its
/// tax registration — normalized per <see cref="TaxIdScheme"/>, with the jurisdiction the number belongs to.
/// Captured and printed only: it never changes the amount charged (no reverse charge until phase 2, § 12 D5).
/// </summary>
public sealed record BusinessBuyer(string CompanyName, string? TaxId, TaxIdScheme? TaxIdScheme, string? TaxIdJurisdiction);

/// <summary>
/// A business-purchase block the checkout refuses. <see cref="Field"/> is one of the <c>BuyerIdentity.*Field</c>
/// names, so the endpoint's 400 problem+json can say which input is at fault.
/// </summary>
public sealed class BuyerIdentityException(string message, string field) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>Resolves the bill-to address and validates the business-purchase block of a checkout (ADR-0063 P3).</summary>
public static class BuyerIdentity
{
    public const string CompanyNameField = "CompanyName";
    public const string TaxIdField = "TaxId";
    public const string TaxIdSchemeField = "TaxIdScheme";
    public const string TaxIdJurisdictionField = "TaxIdJurisdiction";
    public const string BillingAddressField = "BillingAddress";

    /// <summary>Bounds of the persisted columns; checked here so an oversize value is a 400, never a DB error.</summary>
    public const int CompanyNameMaxLength = 200;
    public const int TaxIdMaxLength = 80;

    /// <summary>
    /// The bill-to address: the one supplied, trimmed (country upper-cased, a blank region dropped), or — when the
    /// shopper gave none — a copy of the ship-to address. Never null: every order placed since P3 has a bill-to.
    /// </summary>
    public static BillToAddress ResolveBillTo(BillToAddress shipTo, BillToAddress? supplied)
    {
        var source = supplied ?? shipTo;
        var billTo = new BillToAddress(
            source.Name?.Trim() ?? string.Empty,
            source.Line1?.Trim() ?? string.Empty,
            source.City?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(source.Region) ? null : source.Region.Trim(),
            source.Postcode?.Trim() ?? string.Empty,
            source.Country?.Trim().ToUpperInvariant() ?? string.Empty);

        // The request validator already requires these; defence in depth so a bill-to can never be stored blank.
        if (supplied is not null
            && (billTo.Name.Length == 0 || billTo.Line1.Length == 0 || billTo.City.Length == 0
                || billTo.Postcode.Length == 0 || billTo.Country.Length != 2))
        {
            throw new BuyerIdentityException(
                "The billing address needs a name, street, city, postcode and a 2-letter country.", BillingAddressField);
        }

        return billTo;
    }

    /// <summary>
    /// Validates and normalizes a business-purchase block. The company name is required (≤ 200 characters). The tax
    /// ID is optional, but a tax ID needs its scheme and a scheme needs its tax ID. The number is format-checked per
    /// scheme by <see cref="BuyerTaxIdFormats"/> (no registry lookup — VIES is phase 2). A US state sales-tax ID
    /// without an explicit jurisdiction takes its state from a US bill-to address's region.
    /// </summary>
    public static BusinessBuyer Resolve(
        string? companyName, string? taxId, TaxIdScheme? scheme, string? taxIdJurisdiction, BillToAddress billTo)
    {
        var name = companyName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new BuyerIdentityException("Enter the company name for a business purchase.", CompanyNameField);
        }

        if (name.Length > CompanyNameMaxLength)
        {
            throw new BuyerIdentityException($"The company name can be at most {CompanyNameMaxLength} characters.", CompanyNameField);
        }

        var hasTaxId = !string.IsNullOrWhiteSpace(taxId);
        if (!hasTaxId)
        {
            if (scheme is not null)
            {
                throw new BuyerIdentityException("Enter the tax ID, or clear the tax-ID scheme.", TaxIdField);
            }

            return new BusinessBuyer(name, null, null, null);
        }

        if (scheme is not { } chosen)
        {
            throw new BuyerIdentityException("Choose which register the tax ID belongs to (its scheme).", TaxIdSchemeField);
        }

        if (taxId!.Trim().Length > TaxIdMaxLength)
        {
            throw new BuyerIdentityException($"The tax ID can be at most {TaxIdMaxLength} characters.", TaxIdField);
        }

        var jurisdiction = taxIdJurisdiction;
        if (chosen == TaxIdScheme.UsStateSalesTaxId && string.IsNullOrWhiteSpace(jurisdiction)
            && billTo.Country == "US" && billTo.Region is { Length: 2 } state)
        {
            jurisdiction = "US-" + state.ToUpperInvariant();
        }

        var normalized = BuyerTaxIdFormats.Normalize(chosen, taxId, jurisdiction);
        return new BusinessBuyer(name, normalized.Value, chosen, normalized.Jurisdiction);
    }
}

/// <summary>A normalized buyer tax ID plus the jurisdiction it belongs to (null when the scheme implies none).</summary>
public sealed record NormalizedBuyerTaxId(string Value, string? Jurisdiction);

/// <summary>
/// Per-scheme FORMAT rules for a buyer's tax ID (ADR-0063 P3). A deliberate copy of the Entity service's
/// <c>TaxIdentifierFormats</c> (P2) over the shared <see cref="TaxIdScheme"/> vocabulary: services share contracts,
/// never domain logic (AGENTS.md), so Ordering keeps its own rules, and a unit-lane parity test
/// (<c>BuyerTaxIdFormatParityTests</c>) fails the build when the two copies disagree. Format only — no registry lookup
/// (VIES is phase 2) and no checksum beyond what a scheme's shape implies. Errors name <see cref="BuyerIdentity.TaxIdField"/>,
/// <see cref="BuyerIdentity.TaxIdJurisdictionField"/> or <see cref="BuyerIdentity.TaxIdSchemeField"/>.
/// </summary>
public static partial class BuyerTaxIdFormats
{
    // EU VAT numbers: the VIES Member-State prefix (EL = Greece, XI = Northern Ireland goods) → national shape.
    private static readonly Dictionary<string, Regex> EuVatShapes = new(StringComparer.Ordinal)
    {
        ["AT"] = new(@"^U\d{8}$"),
        ["BE"] = new(@"^[01]\d{9}$"),
        ["BG"] = new(@"^\d{9,10}$"),
        ["CY"] = new(@"^\d{8}[A-Z]$"),
        ["CZ"] = new(@"^\d{8,10}$"),
        ["DE"] = new(@"^\d{9}$"),
        ["DK"] = new(@"^\d{8}$"),
        ["EE"] = new(@"^\d{9}$"),
        ["EL"] = new(@"^\d{9}$"),
        ["ES"] = new(@"^[A-Z0-9]\d{7}[A-Z0-9]$"),
        ["FI"] = new(@"^\d{8}$"),
        ["FR"] = new(@"^[A-HJ-NP-Z0-9]{2}\d{9}$"),
        ["HR"] = new(@"^\d{11}$"),
        ["HU"] = new(@"^\d{8}$"),
        ["IE"] = new(@"^(\d{7}[A-W][A-IW]?|\d[A-Z+*]\d{5}[A-W])$"),
        ["IT"] = new(@"^\d{11}$"),
        ["LT"] = new(@"^(\d{9}|\d{12})$"),
        ["LU"] = new(@"^\d{8}$"),
        ["LV"] = new(@"^\d{11}$"),
        ["MT"] = new(@"^\d{8}$"),
        ["NL"] = new(@"^\d{9}B\d{2}$"),
        ["PL"] = new(@"^\d{10}$"),
        ["PT"] = new(@"^\d{9}$"),
        ["RO"] = new(@"^\d{2,10}$"),
        ["SE"] = new(@"^\d{10}01$"),
        ["SI"] = new(@"^\d{8}$"),
        ["SK"] = new(@"^\d{10}$"),
        ["XI"] = new(@"^(\d{9}|\d{12}|GD\d{3}|HA\d{3})$"),
    };

    // US states + DC + PR: a state sales-tax permit is only meaningful with its state.
    private static readonly HashSet<string> UsStates = new(StringComparer.Ordinal)
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS", "KY",
        "LA", "ME", "MD", "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND",
        "OH", "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY", "DC", "PR",
    };

    /// <summary>Validates and normalizes <paramref name="value"/> for <paramref name="scheme"/>.</summary>
    public static NormalizedBuyerTaxId Normalize(TaxIdScheme scheme, string? value, string? jurisdiction)
    {
        if (!Enum.IsDefined(scheme))
        {
            throw new BuyerIdentityException($"Unknown tax-ID scheme '{(int)scheme}'.", BuyerIdentity.TaxIdSchemeField);
        }

        var raw = value ?? string.Empty;
        var supplied = string.IsNullOrWhiteSpace(jurisdiction) ? null : NormalizeJurisdiction(jurisdiction);
        switch (scheme)
        {
            case TaxIdScheme.Abn:
            case TaxIdScheme.Acn:
                {
                    // Entity's historical rule, unchanged: strip everything but digits, then check the length.
                    var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
                    var expected = scheme == TaxIdScheme.Abn ? 11 : 9;
                    if (digits.Length != expected)
                    {
                        throw Invalid(scheme, $"expected {expected} digits");
                    }

                    return new(digits, Pin(scheme, supplied, "AU"));
                }

            case TaxIdScheme.EuVat:
                {
                    var compact = Compact(raw);
                    if (compact.Length < 4)
                    {
                        throw Invalid(scheme, "expected a 2-letter Member State prefix followed by the national number, e.g. DE123456789");
                    }

                    var prefix = compact[..2];
                    if (prefix == "GR")
                    {
                        throw Invalid(scheme, "Greek VAT numbers use the EL prefix");
                    }

                    if (!EuVatShapes.TryGetValue(prefix, out var shape))
                    {
                        throw Invalid(scheme, $"'{prefix}' is not an EU VAT Member State prefix");
                    }

                    if (!shape.IsMatch(compact[2..]))
                    {
                        throw Invalid(scheme, $"the number does not match the {prefix} format");
                    }

                    var implied = prefix switch { "EL" => "GR", "XI" => "GB-NIR", _ => prefix };
                    if (supplied is not null && supplied != implied)
                    {
                        throw new BuyerIdentityException(
                            $"Jurisdiction '{supplied}' does not match the VAT number's Member State ({implied}).", BuyerIdentity.TaxIdJurisdictionField);
                    }

                    return new(compact, implied);
                }

            case TaxIdScheme.GbVrn:
                {
                    var compact = Compact(raw);
                    var body = compact.StartsWith("GB", StringComparison.Ordinal) ? compact[2..] : compact;
                    if (!GbVrnShape().IsMatch(body))
                    {
                        throw Invalid(scheme, "expected GB followed by 9 or 12 digits (or GD/HA + 3 digits), e.g. GB123456789");
                    }

                    return new("GB" + body, Pin(scheme, supplied, "GB"));
                }

            case TaxIdScheme.NzGst:
                {
                    var compact = Compact(raw);
                    if (!NzGstShape().IsMatch(compact))
                    {
                        throw Invalid(scheme, "expected an 8 or 9 digit GST/IRD number, e.g. 123-456-789");
                    }

                    return new(compact, Pin(scheme, supplied, "NZ"));
                }

            case TaxIdScheme.CaBn:
                {
                    var compact = Compact(raw);
                    if (!CaBnShape().IsMatch(compact))
                    {
                        throw Invalid(scheme, "expected a 9-digit business number, optionally with its program account, e.g. 123456789RT0001");
                    }

                    return new(compact, Pin(scheme, supplied, "CA"));
                }

            case TaxIdScheme.ChUid:
                {
                    var compact = Compact(raw);
                    if (!ChUidShape().IsMatch(compact))
                    {
                        throw Invalid(scheme, "expected CHE followed by 9 digits, optionally MWST/TVA/IVA, e.g. CHE-123.456.789 MWST");
                    }

                    return new(compact, Pin(scheme, supplied, "CH"));
                }

            case TaxIdScheme.UsStateSalesTaxId:
                {
                    if (supplied is null)
                    {
                        throw new BuyerIdentityException(
                            "A US state sales-tax ID needs its state as the jurisdiction, e.g. US-CA.", BuyerIdentity.TaxIdJurisdictionField);
                    }

                    if (!supplied.StartsWith("US-", StringComparison.Ordinal) || !UsStates.Contains(supplied[3..]))
                    {
                        throw new BuyerIdentityException(
                            $"Jurisdiction '{supplied}' is not a US state (expected US-XX, e.g. US-CA).", BuyerIdentity.TaxIdJurisdictionField);
                    }

                    var normalized = raw.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
                    if (!UsStateIdShape().IsMatch(normalized))
                    {
                        throw Invalid(scheme, "expected 2–40 letters, digits or hyphens");
                    }

                    return new(normalized, supplied);
                }

            default:
                {
                    // Gst / OtherTaxRegistration: free-form (Entity's historical rule), jurisdiction optional.
                    var normalized = raw.Trim().ToUpperInvariant();
                    if (normalized.Length is < 2 or > 80)
                    {
                        throw Invalid(scheme, "expected 2–80 characters");
                    }

                    return new(normalized, supplied);
                }
        }
    }

    /// <summary>
    /// Normalizes a tax jurisdiction: an ISO 3166-1 alpha-2 country, optionally followed by an ISO 3166-2
    /// subdivision suffix (<c>AU</c>, <c>US-CA</c>, <c>GB-NIR</c>).
    /// </summary>
    public static string NormalizeJurisdiction(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (!JurisdictionShape().IsMatch(normalized))
        {
            throw new BuyerIdentityException(
                $"Jurisdiction '{value.Trim()}' must be an ISO country code with an optional region, e.g. AU, DE or US-CA.",
                BuyerIdentity.TaxIdJurisdictionField);
        }

        return normalized;
    }

    // Fixed-country schemes: a caller may narrow to a region of that country (CA-QC), never another country.
    private static string Pin(TaxIdScheme scheme, string? supplied, string country)
    {
        if (supplied is null)
        {
            return country;
        }

        if (supplied != country && !supplied.StartsWith(country + "-", StringComparison.Ordinal))
        {
            throw new BuyerIdentityException(
                $"Jurisdiction '{supplied}' does not match a {scheme} identifier ({country}).", BuyerIdentity.TaxIdJurisdictionField);
        }

        return supplied;
    }

    private static string Compact(string value) =>
        new(value.Trim().ToUpperInvariant().Where(c => c is not (' ' or '.' or '-' or '\t')).ToArray());

    private static BuyerIdentityException Invalid(TaxIdScheme scheme, string detail) =>
        new($"Invalid {scheme} tax ID: {detail}.", BuyerIdentity.TaxIdField);

    [GeneratedRegex(@"^(\d{9}|\d{12}|GD\d{3}|HA\d{3})$")]
    private static partial Regex GbVrnShape();

    [GeneratedRegex(@"^\d{8,9}$")]
    private static partial Regex NzGstShape();

    [GeneratedRegex(@"^\d{9}([A-Z]{2}\d{4})?$")]
    private static partial Regex CaBnShape();

    [GeneratedRegex(@"^CHE\d{9}(MWST|TVA|IVA)?$")]
    private static partial Regex ChUidShape();

    [GeneratedRegex(@"^[A-Z0-9-]{2,40}$")]
    private static partial Regex UsStateIdShape();

    [GeneratedRegex(@"^[A-Z]{2}(-[A-Z0-9]{1,3})?$")]
    private static partial Regex JurisdictionShape();
}
