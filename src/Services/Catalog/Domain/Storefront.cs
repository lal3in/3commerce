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
    public StorefrontTaxRegime TaxRegime { get; private set; } = StorefrontTaxRegime.None;
    public int TaxRateBasisPoints { get; private set; }
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
