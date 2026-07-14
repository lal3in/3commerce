namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// The languages a storefront's UI can be presented in (i18n_0). A language is a BCP-47 code and a
/// display label written in that language — deliberately INDEPENDENT of currency/tax: picking a
/// language implies nothing financial (a zh shopper on the AU storefront still pays A$ with GST).
/// Adding a language = one entry here + a matching message catalog in the frontends
/// (src/Storefront/messages/&lt;code&gt;.json). Codes are lower-case; regional forms ("zh-Hant") are
/// allowed by the validator, so a tenant can be more specific than this list without a code change.
/// </summary>
public static class SupportedLanguages
{
    public static readonly IReadOnlyList<SupportedLanguage> All =
    [
        new("en", "English"),
        new("zh", "中文"),
        new("yue", "粵語"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("es", "Español"),
    ];

    public const string Default = "en";

    public static bool IsKnown(string code) =>
        All.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Normalizes a BCP-47 language tag: trims, lower-cases the primary subtag, and validates the
    /// shape (e.g. "en", "zh", "zh-Hant", "pt-BR"). Empty input falls back to the default language.
    /// Shape — not membership — is validated: an unlisted-but-valid tag is accepted so a tenant is
    /// never blocked on this list; the frontend falls back to English when it has no catalog for it.
    /// </summary>
    public static string Normalize(string? code)
    {
        var value = (code ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return Default;
        }

        var subtags = value.Split('-');
        if (subtags.Length > 3 || value.Length > 16)
        {
            throw new CatalogRuleException($"Language '{code}' is not a valid BCP-47 tag.");
        }

        var primary = subtags[0];
        if (primary.Length is < 2 or > 8 || !primary.All(char.IsAsciiLetter))
        {
            throw new CatalogRuleException($"Language '{code}' is not a valid BCP-47 tag.");
        }

        if (subtags.Skip(1).Any(s => s.Length is < 2 or > 8 || !s.All(char.IsAsciiLetterOrDigit)))
        {
            throw new CatalogRuleException($"Language '{code}' is not a valid BCP-47 tag.");
        }

        // Canonical BCP-47 casing: primary subtag lower, script Titlecase, region UPPER.
        var parts = new List<string> { primary.ToLowerInvariant() };
        parts.AddRange(subtags.Skip(1).Select(s => s.Length == 4
            ? char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant()
            : s.ToUpperInvariant()));
        return string.Join('-', parts);
    }
}

/// <param name="Code">BCP-47 language tag, e.g. "en", "zh".</param>
/// <param name="Label">Display label in that language ("English", "中文") — an endonym, so a shopper
/// who cannot read the current UI language can still find their own in the switcher.</param>
public sealed record SupportedLanguage(string Code, string Label);
