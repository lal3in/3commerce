using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// Neutral internal product schema (ADR-0004): supplier-specific data stays in
/// SupplierRef/Attributes so any future feed maps onto this without migration.
/// </summary>
public class Product
{
    public Guid Id { get; init; }
    public Guid TenantId { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public required string Brand { get; set; }
    public string Description { get; set; } = string.Empty;
    public Guid CategoryId { get; set; }
    public ProductKind Kind { get; set; } = ProductKind.Standard;

    /// <summary>Nature of the product (ADR-0028) — not the fulfilment mechanism (that lives on the Offer).</summary>
    public ProductType ProductType { get; set; } = ProductType.Physical;

    /// <summary>
    /// Lifecycle gate for public discovery. An Inactive product stays in the admin catalog
    /// (editable, importable) but is hidden from the public search and product-detail endpoints.
    /// Public endpoints must only serve Active products; admin endpoints stay unfiltered.
    /// </summary>
    public ProductStatus Status { get; set; } = ProductStatus.Active;
    public Dictionary<string, string> Attributes { get; set; } = [];
    public List<string> ImageUrls { get; set; } = [];
    public string? SupplierRef { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<Variant> Variants { get; init; } = [];
    public List<ProductIdentifier> Identifiers { get; init; } = [];
    public List<ProductBundleComponent> BundleComponents { get; init; } = [];

    /// <summary>
    /// Per-destination ship rules (ADR-0008 projected to Ordering). Each rule targets an ISO 3166-1
    /// alpha-2 country (or the <c>*</c> whole-world default) and declares whether the destination's tax
    /// is charged and whether shipping is already covered for this product to that country. EMPTY means
    /// no per-country overrides (worldwide, taxed, shipping charged as normal).
    /// </summary>
    public List<ProductShipRule> ShipRules { get; private set; } = [];

    public void EnsureTenantScoped()
    {
        if (TenantId == Guid.Empty)
        {
            throw new CatalogRuleException("TenantId is required for products.");
        }
    }

    public ProductIdentifier AddIdentifier(ProductIdentifierType type, string value)
    {
        var normalizedValue = NormalizeIdentifierValue(type, value);
        if (Identifiers.Any(i => i.Type == type && i.Value == normalizedValue))
        {
            throw new CatalogRuleException($"Identifier '{type}:{normalizedValue}' already exists on this product.");
        }

        var identifier = new ProductIdentifier
        {
            Id = Guid.CreateVersion7(),
            ProductId = Id,
            Type = type,
            Value = normalizedValue,
        };
        Identifiers.Add(identifier);
        return identifier;
    }

    public ProductBundleComponent AddBundleComponent(Guid componentProductId, Guid? componentVariantId, int quantity)
    {
        if (Kind != ProductKind.Bundle)
        {
            throw new CatalogRuleException("Only bundle products can have bundle components.");
        }

        if (componentProductId == Guid.Empty || componentProductId == Id)
        {
            throw new CatalogRuleException("Bundle component must reference a different product.");
        }

        if (quantity < 1)
        {
            throw new CatalogRuleException("Bundle component quantity must be at least 1.");
        }

        var component = new ProductBundleComponent
        {
            Id = Guid.CreateVersion7(),
            BundleProductId = Id,
            ComponentProductId = componentProductId,
            ComponentVariantId = componentVariantId,
            Quantity = quantity,
        };
        BundleComponents.Add(component);
        return component;
    }

    /// <summary>
    /// Sets the per-country ship rules. Country codes are normalized to upper-case ISO 3166-1 alpha-2
    /// (or the <c>*</c> whole-world sentinel) and de-duplicated by country (last rule wins). A null
    /// argument leaves the current rules untouched so an older admin client that doesn't send the field
    /// can't silently wipe it (mirrors <see cref="Storefront.SetShipToCountries"/>); an empty list clears.
    /// </summary>
    public void SetShipRules(IEnumerable<ProductShipRule>? rules, DateTimeOffset now)
    {
        if (rules is null)
        {
            return;
        }

        var byCountry = new Dictionary<string, ProductShipRule>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.CountryCode))
            {
                continue;
            }

            var code = rule.CountryCode.Trim().ToUpperInvariant();
            if (code != "*" && (code.Length != 2 || code.Any(c => c is < 'A' or > 'Z')))
            {
                throw new CatalogRuleException($"Ship rule country '{rule.CountryCode}' is not a 2-letter ISO country code.");
            }

            // Last rule for a country wins.
            byCountry[code] = rule with { CountryCode = code };
        }

        // '*' (whole-world default) first, then ISO codes in ordinal order.
        ShipRules = byCountry.Values
            .OrderBy(r => r.CountryCode == "*" ? 0 : 1)
            .ThenBy(r => r.CountryCode, StringComparer.Ordinal)
            .ToList();
        UpdatedAt = now;
    }

    private static string NormalizeIdentifierValue(ProductIdentifierType type, string value)
    {
        var normalized = type switch
        {
            ProductIdentifierType.Gtin or ProductIdentifierType.Ean or ProductIdentifierType.Upc => new string(value.Where(char.IsAsciiDigit).ToArray()),
            _ => value.Trim().ToUpperInvariant(),
        };

        if (normalized.Length is < 2 or > 80)
        {
            throw new CatalogRuleException($"Invalid {type} identifier value.");
        }

        return normalized;
    }
}

public class Variant
{
    public Guid Id { get; init; }
    public Guid ProductId { get; init; }
    public required string Sku { get; set; }
    public string? Barcode { get; set; }
    /// <summary>Integer minor units (AGENTS.md invariant) — never floating point.</summary>
    public long PriceMinor { get; set; }
    /// <summary>ISO 4217. Base/default currency + fallback; per-currency prices live in <see cref="Prices"/>.</summary>
    public required string Currency { get; set; }

    /// <summary>Tenant-authored explicit prices per currency (no FX). Empty = only the base <see cref="PriceMinor"/>.</summary>
    public ICollection<VariantPrice> Prices { get; } = new List<VariantPrice>();

    /// <summary>
    /// Read model only (ADR-0028): availability is owned by Fulfillment and projected here via
    /// InventoryAvailabilityChanged. Do not treat as a source of truth — feed stock through the
    /// Fulfillment inventory endpoints, not by editing this.
    /// </summary>
    public int StockQuantity { get; set; }
    public int? WeightGrams { get; set; }
    public int? LengthMm { get; set; }
    public int? WidthMm { get; set; }
    public int? HeightMm { get; set; }
}

public class Category
{
    public Guid Id { get; init; }
    public Guid TenantId { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public Guid? ParentId { get; set; }
    public int SortOrder { get; set; }
}

public sealed class StorefrontNavigationItem
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid StorefrontId { get; init; }
    public Guid CategoryId { get; init; }
    public required string Label { get; init; }
    public int SortOrder { get; init; }
}

public sealed class ProductIdentifier
{
    public Guid Id { get; init; }
    public Guid ProductId { get; init; }
    public ProductIdentifierType Type { get; init; }
    public required string Value { get; init; }
}

public sealed class ProductBundleComponent
{
    public Guid Id { get; init; }
    public Guid BundleProductId { get; init; }
    public Guid ComponentProductId { get; init; }
    public Guid? ComponentVariantId { get; init; }
    public int Quantity { get; init; }
}

public enum ProductKind
{
    Standard = 1,
    Bundle = 2,
    Kit = 3,
}

public sealed class ProductPublication
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid StorefrontId { get; init; }
    public Guid ProductId { get; init; }
    public PublicationState State { get; private set; } = PublicationState.Draft;
    public string? SlugOverride { get; private set; }
    public string? TitleOverride { get; private set; }
    public string? DescriptionOverride { get; private set; }
    public string? SeoTitle { get; private set; }
    public string? SeoDescription { get; private set; }
    public FulfilmentType FulfillmentSource { get; private set; } = FulfilmentType.Unassigned;
    public string? CountryOfOrigin { get; private set; }
    public string? HarmonizedSystemCode { get; private set; }
    public List<ProductPublicationVariant> Variants { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }

    public static ProductPublication Assign(Guid tenantId, Guid storefrontId, Product product, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || storefrontId == Guid.Empty || product.TenantId != tenantId)
        {
            throw new CatalogRuleException("Publication must be tenant-scoped to a storefront and product.");
        }

        var publicationId = Guid.CreateVersion7();
        return new ProductPublication
        {
            Id = publicationId,
            TenantId = tenantId,
            StorefrontId = storefrontId,
            ProductId = product.Id,
            CreatedAt = now,
            UpdatedAt = now,
            Variants = product.Variants.Select(v => new ProductPublicationVariant
            {
                Id = Guid.CreateVersion7(),
                PublicationId = publicationId,
                VariantId = v.Id,
                Visible = true,
            }).ToList(),
        };
    }

    public void SetOverrides(string? slug, string? title, string? description, string? seoTitle, string? seoDescription, DateTimeOffset now)
    {
        SlugOverride = NormalizeOptional(slug, 120);
        TitleOverride = NormalizeOptional(title, 200);
        DescriptionOverride = NormalizeOptional(description, 4000);
        SeoTitle = NormalizeOptional(seoTitle, 70);
        SeoDescription = NormalizeOptional(seoDescription, 180);
        UpdatedAt = now;
    }

    public void SetFulfillment(FulfilmentType source, string? countryOfOrigin, string? harmonizedSystemCode, DateTimeOffset now)
    {
        if (!Enum.IsDefined(source))
        {
            throw new CatalogRuleException($"Unknown fulfillment source '{source}'.");
        }

        FulfillmentSource = source;
        CountryOfOrigin = NormalizeOptional(countryOfOrigin, 2)?.ToUpperInvariant();
        HarmonizedSystemCode = NormalizeOptional(harmonizedSystemCode, 20);
        UpdatedAt = now;
    }

    public ProductPublicationReadiness CheckReadiness(Product product)
    {
        var missing = new List<string>();
        if (product.TenantId != TenantId || product.Id != ProductId)
        {
            missing.Add("matching tenant product");
        }

        if (product.Variants.Count == 0 || !Variants.Any(v => v.Visible && product.Variants.Any(pv => pv.Id == v.VariantId)))
        {
            missing.Add("at least one visible variant");
        }

        if (product.ImageUrls.Count == 0)
        {
            missing.Add("at least one product image");
        }

        if (FulfillmentSource == FulfilmentType.Unassigned)
        {
            missing.Add("assigned fulfillment source");
        }

        return new ProductPublicationReadiness(missing.Count == 0, missing);
    }

    public void Publish(Product product, DateTimeOffset now)
    {
        var readiness = CheckReadiness(product);
        if (!readiness.IsReady)
        {
            throw new CatalogRuleException($"Product publication is missing: {string.Join(", ", readiness.MissingRequirements)}.");
        }

        State = PublicationState.Published;
        PublishedAt ??= now;
        UpdatedAt = now;
    }

    public void Unpublish(DateTimeOffset now)
    {
        State = PublicationState.Draft;
        UpdatedAt = now;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > maxLength)
        {
            throw new CatalogRuleException($"Value must be at most {maxLength} characters.");
        }

        return normalized;
    }
}

public sealed class ProductPublicationVariant
{
    public Guid Id { get; init; }
    public Guid PublicationId { get; init; }
    public Guid VariantId { get; init; }
    public bool Visible { get; set; } = true;
    public string? SkuOverride { get; set; }
}

public sealed record ProductPublicationReadiness(bool IsReady, IReadOnlyList<string> MissingRequirements);

public enum ProductIdentifierType
{
    SupplierSku = 1,
    Gtin = 2,
    Ean = 3,
    Upc = 4,
    Mpn = 5,
}

public enum PublicationState
{
    Draft = 1,
    Published = 2,
}

/// <summary>
/// The customer-facing nature of a product — the browsable "type" a shopper filters the catalog
/// by (ADR-0028). Distinct from ProductKind (Standard/Bundle) and from the Offer's fulfilment
/// mechanics: a Subscription product is billed recurring and a UsageBased product is metered, but
/// those charging details live on the Offer — this is what the catalog surfaces and filters on.
/// Int-backed and additive (no migration to extend). Legacy rows persisted as 0 render as Physical.
/// </summary>
public enum ProductType
{
    Physical = 1,
    Digital = 2,
    Service = 3,
    Bundle = 4,
    Subscription = 5,
    UsageBased = 6,
}

/// <summary>Publication lifecycle gate honoured by the public catalog endpoints. Active is the default.</summary>
public enum ProductStatus
{
    Active = 1,
    Inactive = 2,
}

/// <summary>
/// A per-product, per-destination ship rule. <paramref name="CountryCode"/> is an ISO 3166-1 alpha-2
/// code or the <c>*</c> whole-world default. <paramref name="ChargeDestinationTax"/> = false means the
/// destination's tax is NOT charged for this product to that country; <paramref name="ShippingCovered"/>
/// = true means shipment is already covered so no shipping is charged. A record so EF's
/// <see cref="ValueComparer{T}"/> compares rules structurally.
/// </summary>
public sealed record ProductShipRule(string CountryCode, bool ChargeDestinationTax, bool ShippingCovered);
