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

    /// <summary>Charge cadence (Phase 7): Once for one-time/usage, Monthly/Yearly for subscriptions.</summary>
    public BillingPeriod BillingPeriod { get; private set; } = BillingPeriod.Once;

    /// <summary>Authoritative price for this offer (ADR-0028). Integer minor units. Flat unit/period price.</summary>
    public long PriceMinor { get; private set; }
    public required string Currency { get; init; }

    /// <summary>Graduated tiers for tiered/usage pricing (Phase 7). Empty = flat PriceMinor.</summary>
    public List<OfferPriceTier> PriceTiers { get; init; } = [];

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

    /// <summary>
    /// Set the offer's price model (Phase 7 / mt7_1): pricing model + billing cadence + optional
    /// graduated tiers. Tiers must start at quantity 1, ascend, and carry non-negative unit prices.
    /// </summary>
    public void SetPricing(
        PricingModel model, BillingPeriod period, IReadOnlyList<(int FromQuantity, long UnitPriceMinor)> tiers, DateTimeOffset now)
    {
        if (!Enum.IsDefined(model) || !Enum.IsDefined(period))
        {
            throw new CatalogRuleException("Unknown pricing model or billing period.");
        }

        var ordered = tiers.OrderBy(t => t.FromQuantity).ToList();
        if (ordered.Count > 0)
        {
            if (ordered[0].FromQuantity != 1)
            {
                throw new CatalogRuleException("The first price tier must start at quantity 1.");
            }

            if (ordered.Select(t => t.FromQuantity).Distinct().Count() != ordered.Count
                || ordered.Any(t => t.UnitPriceMinor < 0))
            {
                throw new CatalogRuleException("Price tiers must be distinct and non-negative.");
            }
        }

        PricingModel = model;
        BillingPeriod = period;
        PriceTiers.Clear();
        foreach (var tier in ordered)
        {
            PriceTiers.Add(new OfferPriceTier { Id = Guid.CreateVersion7(), OfferId = Id, FromQuantity = tier.FromQuantity, UnitPriceMinor = tier.UnitPriceMinor });
        }

        UpdatedAt = now;
    }

    /// <summary>
    /// The charge for a quantity (Phase 7): flat <see cref="PriceMinor"/> × qty unless graduated tiers
    /// apply, in which case each quantity block is priced at its tier's unit price. Subscription is a
    /// per-period price (qty 1); recurring billing applies the cadence.
    /// </summary>
    public long PriceFor(int quantity)
    {
        if (quantity <= 0)
        {
            return 0;
        }

        if (PriceTiers.Count == 0)
        {
            return PriceMinor * quantity;
        }

        var tiers = PriceTiers.OrderBy(t => t.FromQuantity).ToList();
        long total = 0;
        for (var i = 0; i < tiers.Count && tiers[i].FromQuantity <= quantity; i++)
        {
            var blockEnd = i + 1 < tiers.Count ? tiers[i + 1].FromQuantity - 1 : quantity;
            var units = Math.Min(quantity, blockEnd) - tiers[i].FromQuantity + 1;
            total += units * tiers[i].UnitPriceMinor;
        }

        return total;
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

/// <summary>A graduated price tier on an Offer (Phase 7): from this quantity, each unit costs UnitPriceMinor.</summary>
public sealed class OfferPriceTier
{
    public Guid Id { get; init; }
    public Guid OfferId { get; init; }
    public int FromQuantity { get; init; }
    public long UnitPriceMinor { get; init; }
}
