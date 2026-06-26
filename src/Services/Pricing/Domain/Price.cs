using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Pricing.Domain;

public sealed class PricingRuleException(string message) : Exception(message);

/// <summary>A graduated price tier (mt7_1): from this quantity, each unit costs UnitPriceMinor.</summary>
public sealed class PriceTier
{
    public Guid Id { get; init; }
    public Guid PriceId { get; init; }
    public int FromQuantity { get; init; }
    public long UnitPriceMinor { get; init; }
}

/// <summary>
/// A price for a product/variant supply line (Phase 7 / mt7_1), owned by the Pricing service: pricing
/// model, billing cadence, a flat amount and optional graduated tiers. The Catalog Offer keeps a flat
/// price for now; this is the dedicated home that recurring/usage charging bills against.
/// </summary>
public sealed class Price
{
    private readonly List<PriceTier> _tiers = [];

    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid ProductId { get; init; }
    public Guid? VariantId { get; init; }
    public Guid? SupplierId { get; init; }
    public PricingModel PricingModel { get; private set; } = PricingModel.OneTime;
    public BillingPeriod BillingPeriod { get; private set; } = BillingPeriod.Once;
    public long AmountMinor { get; private set; }
    public required string Currency { get; init; }
    public IReadOnlyList<PriceTier> Tiers => _tiers;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Price() { }

    public static Price Create(
        Guid tenantId, Guid productId, Guid? variantId, Guid? supplierId, long amountMinor, string currency,
        PricingModel model, BillingPeriod period, IReadOnlyList<(int FromQuantity, long UnitPriceMinor)> tiers, DateTimeOffset now)
    {
        if (amountMinor < 0)
        {
            throw new PricingRuleException("Amount cannot be negative.");
        }

        var price = new Price
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ProductId = productId,
            VariantId = variantId,
            SupplierId = supplierId,
            AmountMinor = amountMinor,
            Currency = currency.ToUpperInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        price.SetModel(model, period, tiers, now);
        return price;
    }

    public void SetModel(
        PricingModel model, BillingPeriod period, IReadOnlyList<(int FromQuantity, long UnitPriceMinor)> tiers, DateTimeOffset now)
    {
        var ordered = tiers.OrderBy(t => t.FromQuantity).ToList();
        if (ordered.Count > 0 && ordered[0].FromQuantity != 1)
        {
            throw new PricingRuleException("The first price tier must start at quantity 1.");
        }

        if (ordered.Any(t => t.UnitPriceMinor < 0) || ordered.Select(t => t.FromQuantity).Distinct().Count() != ordered.Count)
        {
            throw new PricingRuleException("Price tiers must be distinct and non-negative.");
        }

        PricingModel = model;
        BillingPeriod = period;
        _tiers.Clear();
        foreach (var tier in ordered)
        {
            _tiers.Add(new PriceTier { Id = Guid.CreateVersion7(), PriceId = Id, FromQuantity = tier.FromQuantity, UnitPriceMinor = tier.UnitPriceMinor });
        }

        UpdatedAt = now;
    }

    /// <summary>Charge for a quantity: flat AmountMinor × qty, or per-tier-block graduated pricing.</summary>
    public long PriceFor(int quantity)
    {
        if (quantity <= 0)
        {
            return 0;
        }

        if (_tiers.Count == 0)
        {
            return AmountMinor * quantity;
        }

        var ordered = _tiers.OrderBy(t => t.FromQuantity).ToList();
        long total = 0;
        for (var i = 0; i < ordered.Count && ordered[i].FromQuantity <= quantity; i++)
        {
            var blockEnd = i + 1 < ordered.Count ? ordered[i + 1].FromQuantity - 1 : quantity;
            total += (Math.Min(quantity, blockEnd) - ordered[i].FromQuantity + 1) * ordered[i].UnitPriceMinor;
        }

        return total;
    }
}
