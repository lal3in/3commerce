using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ThreeCommerce.Catalog.Domain;

public sealed class Storefront
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Name { get; private set; } = string.Empty;
    public StorefrontState State { get; private set; } = StorefrontState.Draft;
    public StorefrontVisibility Visibility { get; private set; } = StorefrontVisibility.Private;
    public string? AccessPasswordHash { get; private set; }
    public string PublicUrl { get; private set; } = string.Empty;
    public string Currency { get; private set; } = "EUR";

    /// <summary>
    /// The language the storefront UI is presented in by default (BCP-47, i18n_0). Each shopper may
    /// override it for their session (storefront `3c_locale` cookie). Deliberately INDEPENDENT of
    /// Currency/TaxRegime — language implies no financial relationship.
    /// </summary>
    public string DefaultLanguage { get; private set; } = SupportedLanguages.Default;
    public StorefrontTaxRegime TaxRegime { get; private set; } = StorefrontTaxRegime.None;
    public int TaxRateBasisPoints { get; private set; }

    /// <summary>
    /// The ledger accounts this storefront's sales post to (per-storefront books, phase 2). Each store
    /// keeps its own receivable / revenue / tax accounts in its own currency, so balances and P&amp;L are
    /// per-storefront. Auto-derived from the storefront id when left blank and editable at create/update
    /// (<see cref="SetLedgerAccounts"/>). Cash stays per settling provider+currency, not per storefront.
    /// </summary>
    public string ReceivableAccountCode { get; private set; } = string.Empty;
    public string RevenueAccountCode { get; private set; } = string.Empty;
    public string TaxAccountCode { get; private set; } = string.Empty;
    public string ShippingAccountCode { get; private set; } = string.Empty;

    /// <summary>
    /// Per-storefront theme tokens (mt5_6) as a compact JSON object of sanitized string values, or "" when
    /// unthemed (the storefront renders the default look). Only the six known design tokens are allowed and
    /// each value is validated against the SAME allow/deny rules the storefront app applies client-side
    /// (<c>src/Storefront/lib/theme.ts</c>), so a stored theme can never smuggle in CSS/JS. Rides on the
    /// public config the storefront fetches (not <c>StorefrontConfigChanged</c> — Payments never needs it).
    /// </summary>
    public string ThemeJson { get; private set; } = "";

    /// <summary>
    /// Per-storefront cost-ASSUMPTION rates (basis points) driving the Financials estimated-margin view
    /// (phase 4). Config only — these NEVER post to the ledger (fake journal entries would corrupt the
    /// append-only books); Financials reads them to render an estimated overhead/margin column that is
    /// visually distinct from posted figures. A canonical JSON object of the known bps keys, or "".
    /// </summary>
    public string CostAssumptionsJson { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public List<StorefrontDomain> Domains { get; private set; } = [];

    private Storefront()
    {
    }

    public static Storefront Create(Guid tenantId, string name, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new CatalogRuleException("TenantId is required.");
        }

        return new Storefront
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = NormalizeName(name),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void ConfigureCommerce(string publicUrl, string currency, StorefrontTaxRegime taxRegime, int taxRateBasisPoints, DateTimeOffset now)
    {
        PublicUrl = NormalizePublicUrl(publicUrl);
        Currency = NormalizeCurrency(currency);
        if (!Enum.IsDefined(taxRegime))
        {
            throw new CatalogRuleException($"Unknown storefront tax regime '{taxRegime}'.");
        }

        if (taxRateBasisPoints is < 0 or > 10000)
        {
            throw new CatalogRuleException("Tax rate basis points must be between 0 and 10000.");
        }

        TaxRegime = taxRegime;
        TaxRateBasisPoints = taxRegime == StorefrontTaxRegime.None ? 0 : taxRateBasisPoints;
        UpdatedAt = now;
    }

    /// <summary>
    /// Sets the storefront's ledger accounts (phase 2). A null/blank code auto-derives a stable default
    /// from the storefront id (<c>{kind}.store-{id8}</c>), so a store always has all three; a non-blank
    /// code is normalized (trimmed, lowercased) and overrides the default. Safe to call at create and on
    /// every update — an omitted (null) code re-derives the default rather than wiping it.
    /// </summary>
    public void SetLedgerAccounts(string? receivable, string? revenue, string? tax, DateTimeOffset now, string? shipping = null)
    {
        ReceivableAccountCode = NormalizeAccount(receivable) ?? DefaultAccountCode("receivable");
        RevenueAccountCode = NormalizeAccount(revenue) ?? DefaultAccountCode("revenue");
        TaxAccountCode = NormalizeAccount(tax) ?? DefaultAccountCode("tax");
        ShippingAccountCode = NormalizeAccount(shipping) ?? DefaultAccountCode("shipping");
        UpdatedAt = now;
    }

    /// <summary>
    /// The auto-derived default account code for a kind (receivable/revenue/tax) — stable and UNIQUE per
    /// store. Uses the full id (UUIDv7's first hex chars are a shared creation-time prefix, so a short
    /// slice collides for stores created together); operators typically override to a friendly code.
    /// </summary>
    public string DefaultAccountCode(string kind) => $"{kind}.store-{Id:N}";

    private static string? NormalizeAccount(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var normalized = code.Trim().ToLowerInvariant();
        if (normalized.Length > 80 || !normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            throw new CatalogRuleException("Ledger account code must be up to 80 chars of letters, digits, '.', '-' or '_'.");
        }

        return normalized;
    }

    /// <summary>
    /// Sets the storefront's default UI language (i18n_0). Separate from <see cref="ConfigureCommerce"/>
    /// on purpose: language is presentation, not commerce — changing it must never touch currency or tax.
    /// A null/blank value leaves the current language untouched (so a caller that doesn't know about
    /// languages — e.g. an older admin client PUT — cannot silently reset it).
    /// </summary>
    public void SetDefaultLanguage(string? language, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        DefaultLanguage = SupportedLanguages.Normalize(language);
        UpdatedAt = now;
    }

    // The six design tokens a storefront may override (mt5_6) — mirrors ThemeTokens in
    // src/Storefront/lib/theme.ts. Any other key is rejected so the stored theme can't grow arbitrary CSS.
    private static readonly string[] ThemeTokenKeys =
        ["colorPrimary", "colorBg", "colorText", "colorMuted", "fontSans", "radius"];

    // Cost-assumption bps keys (phase 4). Each is an integer 0–10000 basis points.
    private static readonly string[] CostAssumptionKeys =
        ["packagingBps", "laborBps", "marketingBps", "insuranceBps", "bufferBps"];

    // Ported VERBATIM from src/Storefront/lib/theme.ts:23-24 (the client sanitizer). SAFE_VALUE is the
    // allow-list of characters; DANGEROUS is the deny-list that blocks url()/expression/js/@import and any
    // rule-breaking punctuation. A token must pass BOTH — exactly like safeTokenValue() client-side.
    private static readonly Regex ThemeSafeValue = new(@"^[#a-zA-Z0-9.,()%\s_-]+$", RegexOptions.Compiled);
    private static readonly Regex ThemeDangerous = new(@"url\(|expression|javascript:|@import|[;{}<>]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Sets the storefront's theme tokens (mt5_6). The input must be a JSON object containing only the six
    /// known tokens, each a string ≤100 chars that passes the storefront's own sanitizer. A null/blank value
    /// leaves the current theme untouched (the <see cref="SetDefaultLanguage"/> convention — an older client
    /// that doesn't send a theme can't wipe it). Invalid input throws <see cref="CatalogRuleException"/>.
    /// </summary>
    public void SetTheme(string? themeJson, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(themeJson))
        {
            return;
        }

        ThemeJson = NormalizeTheme(themeJson);
        UpdatedAt = now;
    }

    // Parse → validate → re-serialize a canonical theme object. Keeps only sanitized known tokens so what's
    // stored is exactly what the storefront will render (defence-in-depth: the client sanitizes again).
    private static string NormalizeTheme(string themeJson)
    {
        List<KeyValuePair<string, JsonNode?>> entries;
        try
        {
            if (JsonNode.Parse(themeJson) is not JsonObject obj)
            {
                throw new CatalogRuleException("Theme must be a JSON object of design tokens.");
            }

            // JsonObject's backing dictionary is lazy: duplicate keys in the raw JSON surface as an
            // ArgumentException only at the FIRST enumeration, so materialize HERE, inside the guard —
            // otherwise the foreach below would throw unhandled. (CatalogRuleException derives from
            // InvalidOperationException, so the throw above is NOT swallowed by these catches.)
            entries = [.. obj];
        }
        catch (JsonException)
        {
            throw new CatalogRuleException("Theme must be a valid JSON object.");
        }
        catch (ArgumentException)
        {
            throw new CatalogRuleException("Theme must not contain duplicate tokens.");
        }

        var tokens = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            if (Array.IndexOf(ThemeTokenKeys, key) < 0)
            {
                throw new CatalogRuleException($"Unknown theme token '{key}'.");
            }

            if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var token))
            {
                throw new CatalogRuleException($"Theme token '{key}' must be a string.");
            }

            var trimmed = token.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 100 || ThemeDangerous.IsMatch(trimmed) || !ThemeSafeValue.IsMatch(trimmed))
            {
                throw new CatalogRuleException($"Theme token '{key}' has an unsafe or overlong value.");
            }

            tokens[key] = trimmed;
        }

        return JsonSerializer.Serialize(tokens);
    }

    /// <summary>
    /// Sets the per-storefront cost-assumption rates (phase 4). A canonical JSON object of the known bps
    /// keys, each an integer 0–10000. A null/blank value leaves the current assumptions untouched (the
    /// <see cref="SetDefaultLanguage"/>/<see cref="SetTheme"/> convention). Invalid input throws
    /// <see cref="CatalogRuleException"/>. Config only — never posts to the ledger.
    /// </summary>
    public void SetCostAssumptions(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        CostAssumptionsJson = NormalizeCostAssumptions(json);
        UpdatedAt = now;
    }

    // Parse → validate → re-serialize a canonical bps object (mirrors NormalizeTheme, including the
    // duplicate-key enumeration guard). Only known keys, each an integer in [0, 10000].
    private static string NormalizeCostAssumptions(string json)
    {
        List<KeyValuePair<string, JsonNode?>> entries;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
            {
                throw new CatalogRuleException("Cost assumptions must be a JSON object of bps rates.");
            }

            entries = [.. obj];
        }
        catch (JsonException)
        {
            throw new CatalogRuleException("Cost assumptions must be a valid JSON object.");
        }
        catch (ArgumentException)
        {
            throw new CatalogRuleException("Cost assumptions must not contain duplicate keys.");
        }

        var rates = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            if (Array.IndexOf(CostAssumptionKeys, key) < 0)
            {
                throw new CatalogRuleException($"Unknown cost-assumption key '{key}'.");
            }

            if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<int>(out var bps))
            {
                throw new CatalogRuleException($"Cost-assumption '{key}' must be an integer basis-points value.");
            }

            if (bps is < 0 or > 10000)
            {
                throw new CatalogRuleException($"Cost-assumption '{key}' must be between 0 and 10000 basis points.");
            }

            rates[key] = bps;
        }

        return JsonSerializer.Serialize(rates);
    }

    /// <summary>
    /// Clones the shopper-facing configuration of <paramref name="source"/> into a brand-new storefront:
    /// same currency/tax/language/theme, but with FRESH auto-derived ledger accounts (the safety invariant —
    /// <see cref="SetLedgerAccounts"/>(null,…) derives <c>{kind}.store-{newId:N}</c> from the new id, so the
    /// clone's books never share the source's). Deliberately NOT copied: PublicUrl, domains, visibility
    /// (stays Private), state (stays Draft), access password, account-code strings. Product publications and
    /// navigation are copied by the caller (they live in other aggregates). Never copy account-code strings.
    /// </summary>
    public static Storefront DuplicateFrom(Storefront source, string name, DateTimeOffset now)
    {
        var clone = Create(source.TenantId, name, now);
        clone.ConfigureCommerce(string.Empty, source.Currency, source.TaxRegime, source.TaxRateBasisPoints, now);
        clone.SetDefaultLanguage(source.DefaultLanguage, now);
        clone.SetTheme(source.ThemeJson, now);
        clone.SetCostAssumptions(source.CostAssumptionsJson, now);
        clone.SetLedgerAccounts(null, null, null, now, null);
        return clone;
    }

    public void Rename(string name, DateTimeOffset now)
    {
        Name = NormalizeName(name);
        UpdatedAt = now;
    }

    public void SetVisibility(StorefrontVisibility visibility, string? accessPasswordHash, DateTimeOffset now)
    {
        if (!Enum.IsDefined(visibility))
        {
            throw new CatalogRuleException($"Unknown storefront visibility '{visibility}'.");
        }

        if (visibility == StorefrontVisibility.Password && string.IsNullOrWhiteSpace(accessPasswordHash))
        {
            throw new CatalogRuleException("Password storefronts require an access password hash.");
        }

        Visibility = visibility;
        AccessPasswordHash = visibility == StorefrontVisibility.Password ? accessPasswordHash : null;
        UpdatedAt = now;
    }

    public StorefrontDomain AddDomain(string host, bool canonical, DateTimeOffset now)
    {
        var normalizedHost = NormalizeHost(host);
        if (Domains.Any(d => string.Equals(d.Host, normalizedHost, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CatalogRuleException($"Domain '{normalizedHost}' is already assigned to this storefront.");
        }

        if (canonical)
        {
            foreach (var domain in Domains.Where(d => d.Canonical))
            {
                domain.UnsetCanonical();
            }
        }

        var newDomain = new StorefrontDomain
        {
            Id = Guid.CreateVersion7(),
            StorefrontId = Id,
            Host = normalizedHost,
            Canonical = canonical,
            CreatedAt = now,
        };
        Domains.Add(newDomain);
        UpdatedAt = now;
        return newDomain;
    }

    public StorefrontReadinessResult CheckReadiness()
    {
        var missing = new List<string>();
        if (Domains.Count == 0)
        {
            missing.Add("at least one domain");
        }

        if (!Domains.Any(d => d.Canonical))
        {
            missing.Add("one canonical domain");
        }

        if (Visibility is StorefrontVisibility.Private or StorefrontVisibility.InviteOnly)
        {
            missing.Add("public or password visibility for live selling");
        }

        return new StorefrontReadinessResult(missing.Count == 0, missing);
    }

    public void MoveToPreview(DateTimeOffset now)
    {
        EnsureState(StorefrontState.Draft, StorefrontState.Paused);
        State = StorefrontState.Preview;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        EnsureState(StorefrontState.Preview, StorefrontState.Paused);
        var readiness = CheckReadiness();
        if (!readiness.IsReady)
        {
            throw new CatalogRuleException($"Storefront is missing: {string.Join(", ", readiness.MissingRequirements)}.");
        }

        State = StorefrontState.Active;
        ActivatedAt ??= now;
        UpdatedAt = now;
    }

    public void Pause(DateTimeOffset now)
    {
        EnsureState(StorefrontState.Draft, StorefrontState.Preview, StorefrontState.Active);
        State = StorefrontState.Paused;
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        if (State == StorefrontState.Archived)
        {
            return;
        }

        State = StorefrontState.Archived;
        UpdatedAt = now;
    }

    private void EnsureState(params StorefrontState[] allowed)
    {
        if (!allowed.Contains(State))
        {
            throw new CatalogRuleException($"Storefront state {State} cannot perform this transition.");
        }
    }

    private static string NormalizeName(string name)
    {
        var value = name.Trim();
        if (value.Length is < 2 or > 120)
        {
            throw new CatalogRuleException("Storefront name must be between 2 and 120 characters.");
        }

        return value;
    }

    private static string NormalizeHost(string host)
    {
        var value = host.Trim().ToLowerInvariant();
        if (value.Length is < 3 or > 253 || value.Contains('/'))
        {
            throw new CatalogRuleException("Storefront domain host is invalid.");
        }

        return value;
    }

    private static string NormalizePublicUrl(string publicUrl)
    {
        var value = publicUrl.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new CatalogRuleException("Storefront public URL must be an absolute http(s) URL.");
        }

        return value.TrimEnd('/');
    }

    private static string NormalizeCurrency(string currency)
    {
        var value = currency.Trim().ToUpperInvariant();
        if (value.Length != 3 || value.Any(c => c is < 'A' or > 'Z'))
        {
            throw new CatalogRuleException("Currency must be a 3-letter ISO currency code.");
        }

        return value;
    }
}

public enum StorefrontTaxRegime
{
    None = 0,
    AuGst = 1,
    EuVat = 2,
    UsSalesTax = 3,
    Other = 99,
}

public sealed class StorefrontDomain
{
    public Guid Id { get; init; }
    public Guid StorefrontId { get; init; }
    public required string Host { get; init; }
    public bool Canonical { get; set; }
    public DateTimeOffset CreatedAt { get; init; }

    public void UnsetCanonical() => Canonical = false;
}

public sealed record StorefrontReadinessResult(bool IsReady, IReadOnlyList<string> MissingRequirements);

public enum StorefrontState
{
    Draft = 1,
    Preview = 2,
    Active = 3,
    Paused = 4,
    Archived = 5,
}

public enum StorefrontVisibility
{
    Private = 1,
    Password = 2,
    InviteOnly = 3,
    Public = 4,
}
