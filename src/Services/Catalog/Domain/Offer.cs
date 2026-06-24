using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Catalog.Domain;

public enum OfferStatus { Active = 1, Inactive = 2 }

/// <summary>
/// An Offer (product supply profile, ADR-0028): how a (product, variant) is sourced from a
/// specific supplier — supply category + fulfilment type — and the price it sells at. A product
/// or variant may have many offers (multi-supplier); checkout/publication selects one. The Offer
/// is the authoritative price owner going forward (price moves off the variant).
/// </summary>
public sealed class Offer
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid ProductId { get; init; }

    /// <summary>Null = product-level offer (single-SKU product or all variants).</summary>
    public Guid? VariantId { get; init; }

    /// <summary>The supplying party (Entity supplier). Supersedes the single Product.SupplierRef.</summary>
    public Guid SupplierId { get; init; }

    public SupplyCategory SupplyCategory { get; init; }
    public FulfilmentType FulfilmentType { get; init; }
    public PricingModel PricingModel { get; private set; } = PricingModel.OneTime;

    /// <summary>Authoritative price for this offer (ADR-0028). Integer minor units.</summary>
    public long PriceMinor { get; private set; }
    public required string Currency { get; init; }

    /// <summary>Lower is preferred when several offers can fulfil a line.</summary>
    public int Priority { get; private set; }

    public OfferStatus Status { get; private set; } = OfferStatus.Active;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == OfferStatus.Active;

    private Offer() { }

    public static Offer Create(
        Guid tenantId, Guid productId, Guid? variantId, Guid supplierId,
        SupplyCategory supplyCategory, FulfilmentType fulfilmentType,
        long priceMinor, string currency, int priority, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || productId == Guid.Empty || supplierId == Guid.Empty)
        {
            throw new CatalogRuleException("Offer tenant, product, and supplier are required.");
        }

        if (!Enum.IsDefined(supplyCategory) || !Enum.IsDefined(fulfilmentType))
        {
            throw new CatalogRuleException("Offer supply category and fulfilment type must be valid.");
        }

        if (!IsCompatible(supplyCategory, fulfilmentType))
        {
            throw new CatalogRuleException(
                $"Fulfilment type '{fulfilmentType}' is not valid for supply category '{supplyCategory}'.");
        }

        if (priceMinor < 0)
        {
            throw new CatalogRuleException("Offer price cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new CatalogRuleException("Offer currency is required.");
        }

        return new Offer
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ProductId = productId,
            VariantId = variantId,
            SupplierId = supplierId,
            SupplyCategory = supplyCategory,
            FulfilmentType = fulfilmentType,
            PriceMinor = priceMinor,
            Currency = currency.ToUpperInvariant(),
            Priority = priority,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void SetPrice(long priceMinor, DateTimeOffset now)
    {
        if (priceMinor < 0)
        {
            throw new CatalogRuleException("Offer price cannot be negative.");
        }

        PriceMinor = priceMinor;
        UpdatedAt = now;
    }

    public void SetPriority(int priority, DateTimeOffset now)
    {
        Priority = priority;
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        Status = OfferStatus.Inactive;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        Status = OfferStatus.Active;
        UpdatedAt = now;
    }

    /// <summary>
    /// The supply category constrains which fulfilment mechanisms are valid (ADR-0028):
    /// physical ships, digital is delivered electronically, service is performed.
    /// </summary>
    private static bool IsCompatible(SupplyCategory category, FulfilmentType type) => category switch
    {
        SupplyCategory.Physical => type is FulfilmentType.Warehouse or FulfilmentType.Dropship or FulfilmentType.Unassigned,
        SupplyCategory.Digital => type is FulfilmentType.DigitalDownload or FulfilmentType.Subscription or FulfilmentType.Usage,
        SupplyCategory.Service => type is FulfilmentType.ManualService,
        _ => false,
    };
}
