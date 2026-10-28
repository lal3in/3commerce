using System.Text.RegularExpressions;

namespace ThreeCommerce.Entity.Domain;

/// <summary>A normalized identifier value plus the tax jurisdiction it belongs to (null when unknown).</summary>
public sealed record NormalizedTaxIdentifier(string Value, string? Jurisdiction);

/// <summary>
/// Per-scheme format rules for entity identifiers (ADR-0063 § 7.3, P2). Format only: no registry lookup
/// here (VIES verification is phase 2), and no checksum beyond what a scheme's shape implies. ABN/ACN keep
/// their historical rule (digits only, 11 / 9 digits) so every identifier stored before P2 stays valid.
/// A violation throws <see cref="DomainRuleException"/> whose <see cref="DomainRuleException.Field"/> is
/// <see cref="ValueField"/> or <see cref="JurisdictionField"/>, so the endpoint's 400 names the field.
/// </summary>
public static partial class TaxIdentifierFormats
{
    public const string ValueField = "Value";
    public const string JurisdictionField = "Jurisdiction";

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

    /// <summary>Validates and normalizes <paramref name="value"/> for <paramref name="type"/>.</summary>
    public static NormalizedTaxIdentifier Normalize(EntityIdentifierType type, string? value, string? jurisdiction)
    {
        if (!Enum.IsDefined(type))
        {
            throw new DomainRuleException($"Unknown identifier type '{type}'.", "Type");
        }

        var raw = value ?? string.Empty;
        var supplied = string.IsNullOrWhiteSpace(jurisdiction) ? null : NormalizeJurisdiction(jurisdiction, JurisdictionField);
        switch (type)
        {
            case EntityIdentifierType.Abn:
            case EntityIdentifierType.Acn:
                {
                    // Historical rule, unchanged: strip everything but digits, then check the length.
                    var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
                    var expected = type == EntityIdentifierType.Abn ? 11 : 9;
                    if (digits.Length != expected)
                    {
                        throw Invalid(type, $"expected {expected} digits");
                    }

                    return new(digits, Pin(type, supplied, "AU"));
                }

            case EntityIdentifierType.EuVat:
                {
                    var compact = Compact(raw);
                    if (compact.Length < 4)
                    {
                        throw Invalid(type, "expected a 2-letter Member State prefix followed by the national number, e.g. DE123456789");
                    }

                    var prefix = compact[..2];
                    if (prefix == "GR")
                    {
                        throw Invalid(type, "Greek VAT numbers use the EL prefix");
                    }

                    if (!EuVatShapes.TryGetValue(prefix, out var shape))
                    {
                        throw Invalid(type, $"'{prefix}' is not an EU VAT Member State prefix");
                    }

                    if (!shape.IsMatch(compact[2..]))
                    {
                        throw Invalid(type, $"the number does not match the {prefix} format");
                    }

                    var implied = prefix switch { "EL" => "GR", "XI" => "GB-NIR", _ => prefix };
                    if (supplied is not null && supplied != implied)
                    {
                        throw new DomainRuleException(
                            $"Jurisdiction '{supplied}' does not match the VAT number's Member State ({implied}).", JurisdictionField);
                    }

                    return new(compact, implied);
                }

            case EntityIdentifierType.GbVrn:
                {
                    var compact = Compact(raw);
                    var body = compact.StartsWith("GB", StringComparison.Ordinal) ? compact[2..] : compact;
                    if (!GbVrnShape().IsMatch(body))
                    {
                        throw Invalid(type, "expected GB followed by 9 or 12 digits (or GD/HA + 3 digits), e.g. GB123456789");
                    }

                    return new("GB" + body, Pin(type, supplied, "GB"));
                }

            case EntityIdentifierType.NzGst:
                {
                    var compact = Compact(raw);
                    if (!NzGstShape().IsMatch(compact))
                    {
                        throw Invalid(type, "expected an 8 or 9 digit GST/IRD number, e.g. 123-456-789");
                    }

                    return new(compact, Pin(type, supplied, "NZ"));
                }

            case EntityIdentifierType.CaBn:
                {
                    var compact = Compact(raw);
                    if (!CaBnShape().IsMatch(compact))
                    {
                        throw Invalid(type, "expected a 9-digit business number, optionally with its program account, e.g. 123456789RT0001");
                    }

                    return new(compact, Pin(type, supplied, "CA"));
                }

            case EntityIdentifierType.ChUid:
                {
                    var compact = Compact(raw);
                    if (!ChUidShape().IsMatch(compact))
                    {
                        throw Invalid(type, "expected CHE followed by 9 digits, optionally MWST/TVA/IVA, e.g. CHE-123.456.789 MWST");
                    }

                    return new(compact, Pin(type, supplied, "CH"));
                }

            case EntityIdentifierType.UsStateSalesTaxId:
                {
                    if (supplied is null)
                    {
                        throw new DomainRuleException(
                            "A US state sales-tax ID needs its state as the jurisdiction, e.g. US-CA.", JurisdictionField);
                    }

                    if (!supplied.StartsWith("US-", StringComparison.Ordinal) || !UsStates.Contains(supplied[3..]))
                    {
                        throw new DomainRuleException(
                            $"Jurisdiction '{supplied}' is not a US state (expected US-XX, e.g. US-CA).", JurisdictionField);
                    }

                    var normalized = raw.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
                    if (!UsStateIdShape().IsMatch(normalized))
                    {
                        throw Invalid(type, "expected 2–40 letters, digits or hyphens");
                    }

                    return new(normalized, supplied);
                }

            default:
                {
                    // Gst / OtherTaxRegistration: free-form (historical rule), jurisdiction optional.
                    var normalized = raw.Trim().ToUpperInvariant();
                    if (normalized.Length is < 2 or > 80)
                    {
                        throw Invalid(type, "expected 2–80 characters");
                    }

                    return new(normalized, supplied);
                }
        }
    }

    /// <summary>
    /// The jurisdiction an identifier stored before P2 (no Jurisdiction column value) belongs to, derived
    /// from its scheme and value; null when the scheme does not imply one (Gst / Other).
    /// </summary>
    public static string? ImpliedJurisdiction(EntityIdentifierType type, string value) => type switch
    {
        EntityIdentifierType.Abn or EntityIdentifierType.Acn => "AU",
        EntityIdentifierType.GbVrn => "GB",
        EntityIdentifierType.NzGst => "NZ",
        EntityIdentifierType.CaBn => "CA",
        EntityIdentifierType.ChUid => "CH",
        EntityIdentifierType.EuVat when value.Length >= 2 => value[..2] switch { "EL" => "GR", "XI" => "GB-NIR", var p => p },
        _ => null,
    };

    /// <summary>
    /// Normalizes a tax jurisdiction: an ISO 3166-1 alpha-2 country, optionally followed by an ISO 3166-2
    /// subdivision suffix (<c>AU</c>, <c>US-CA</c>, <c>GB-NIR</c>). Throws naming <paramref name="field"/>.
    /// </summary>
    public static string NormalizeJurisdiction(string value, string field)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (!JurisdictionShape().IsMatch(normalized))
        {
            throw new DomainRuleException(
                $"Jurisdiction '{value.Trim()}' must be an ISO country code with an optional region, e.g. AU, DE or US-CA.", field);
        }

        return normalized;
    }

    // Fixed-country schemes: a caller may narrow to a region of that country (CA-QC), never another country.
    private static string Pin(EntityIdentifierType type, string? supplied, string country)
    {
        if (supplied is null)
        {
            return country;
        }

        if (supplied != country && !supplied.StartsWith(country + "-", StringComparison.Ordinal))
        {
            throw new DomainRuleException($"Jurisdiction '{supplied}' does not match a {type} identifier ({country}).", JurisdictionField);
        }

        return supplied;
    }

    private static string Compact(string value) =>
        new(value.Trim().ToUpperInvariant().Where(c => c is not (' ' or '.' or '-' or '\t')).ToArray());

    private static DomainRuleException Invalid(EntityIdentifierType type, string detail) =>
        new($"Invalid {type} identifier value: {detail}.", ValueField);

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
