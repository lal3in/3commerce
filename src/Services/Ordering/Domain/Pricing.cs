using ThreeCommerce.BuildingBlocks.Contracts.Catalog;

namespace ThreeCommerce.Ordering.Domain;

public enum TaxMode
{
    Exclusive = 1,
    Inclusive = 2,
    Exempt = 3,
}

public enum PromotionKind
{
    CouponFixed = 1,
    CouponPercent = 2,
    AutomaticProduct = 3,
    AutomaticCategory = 4,
    AutomaticStorefront = 5,
    BundleDiscount = 6,
    FreeShipping = 7,
    QuantityTier = 8,

    /// <summary>
    /// Threshold promotion (ADR-0051): free shipping and/or a discount once the cart (storefront scope)
    /// or one product's lines (product scope) clear a money and/or quantity threshold. Both thresholds
    /// set are ANDed. This is the kind Catalog authors and projects into <see cref="PromotionCopy"/>;
    /// kinds 1-8 predate it and are engine-only.
    /// </summary>
    Threshold = 9,
}

public sealed record PricingInput(
    Guid TenantId,
    Guid StorefrontId,
    string Currency,
    IReadOnlyList<PricingLineInput> Lines,
    long ShippingMinor,
    string? CouponCode = null,
    string? ShipCountry = null,
    // Storefront-wide discount in basis points (0–10000; 0 = none). Deducted from the items' subtotal only
    // (never shipping, never tax), applied AFTER the per-line offer/catalog price is already reflected in
    // each line's SellingPriceMinor. Stacks additively with any promotion; the total is capped at subtotal.
    int StorefrontDiscountBps = 0,
    // Evaluation time for a Threshold promotion's active window (ADR-0051). Null = windows are not checked
    // (the engine is otherwise time-free); checkout always supplies it.
    DateTimeOffset? Now = null);

public sealed record PricingLineInput(
    Guid ProductId,
    Guid? CategoryId,
    Guid? VariantId,
    long SupplierCostMinor,
    long SellingPriceMinor,
    int Quantity,
    TaxMode TaxMode);

public sealed record Promotion(
    Guid Id,
    Guid TenantId,
    Guid? StorefrontId,
    PromotionKind Kind,
    long AmountMinor = 0,
    int PercentOff = 0,
    string? CouponCode = null,
    Guid? ProductId = null,
    Guid? CategoryId = null,
    Guid? BundleProductId = null,
    int MinimumQuantity = 0,
    bool Active = true,
    // Threshold promotions (ADR-0051), appended so every existing positional construction still compiles.
    // MinimumAmountMinor is the money threshold on the promotion's scope base (0 = none; ANDed with
    // MinimumQuantity when both are set). Combinable = stacks with other combinable promotions
    // (false = Exclusive). GrantsFreeShipping zeroes the shipping charge. Currency/ActiveFrom/ActiveUntil
    // mirror the projected copy's fields; the engine's own currency comes from the PricingInput.
    long MinimumAmountMinor = 0,
    bool Combinable = false,
    bool GrantsFreeShipping = false,
    string Currency = "",
    DateTimeOffset? ActiveFrom = null,
    DateTimeOffset? ActiveUntil = null)
{
    public void Validate(string currency)
    {
        if (TenantId == Guid.Empty)
        {
            throw new PricingRuleException("Promotion tenant is required.");
        }

        if (AmountMinor < 0 || PercentOff is < 0 or > 100 || MinimumQuantity < 0 || MinimumAmountMinor < 0)
        {
            throw new PricingRuleException("Promotion discount values are invalid.");
        }

        if (Kind == PromotionKind.QuantityTier && (MinimumQuantity < 2 || ProductId is null && CategoryId is null))
        {
            throw new PricingRuleException("Quantity-tier promotions require a minimum quantity and product or category scope.");
        }

        if (Kind == PromotionKind.Threshold)
        {
            if (MinimumAmountMinor == 0 && MinimumQuantity == 0)
            {
                throw new PricingRuleException("Threshold promotions require a minimum amount or a minimum quantity.");
            }

            if (!GrantsFreeShipping && PercentOff == 0 && AmountMinor == 0)
            {
                throw new PricingRuleException("Threshold promotions require a reward: free shipping, a percent off, or a fixed discount.");
            }

            if (ProductId is null && CategoryId is not null)
            {
                throw new PricingRuleException("Threshold promotions are scoped to the storefront or to a product, never a category.");
            }
        }

        if (currency.Length != 3)
        {
            throw new PricingRuleException("Promotion currency must be an ISO 4217 code.");
        }
    }
}

public sealed record PricingResult(
    long SubtotalMinor,
    long DiscountMinor,
    long ShippingMinor,
    long TaxMinor,
    long GrossMinor,
    string Currency,
    // Every promotion that won, in ascending-id order (combinable promotions stack — ADR-0051).
    IReadOnlyList<Guid> AppliedPromotionIds,
    bool FreeShippingApplied,
    // The winning discount spread over the input lines (largest remainder); sums to the promotion part of
    // DiscountMinor exactly. Empty when no promotion applied.
    IReadOnlyList<long> LinePromotionDiscountsMinor)
{
    /// <summary>
    /// The first applied promotion, or null when none applied. Kept as a computed convenience for callers
    /// (and tests) that predate stacking — a combinable win reports several ids in
    /// <see cref="AppliedPromotionIds"/>.
    /// </summary>
    public Guid? AppliedPromotionId => AppliedPromotionIds.Count > 0 ? AppliedPromotionIds[0] : null;
}

public interface ITaxStrategy
{
    public long TaxFor(TaxCalculationInput input);
}

public sealed record TaxCalculationInput(
    string Currency,
    string? ShipCountry,
    IReadOnlyList<PricingLineInput> Lines,
    long DiscountMinor);

public sealed class ZeroTaxStrategy : ITaxStrategy
{
    public static ZeroTaxStrategy Instance { get; } = new();

    private ZeroTaxStrategy()
    {
    }

    public long TaxFor(TaxCalculationInput input) => 0;
}

public sealed class HomeRegimeTaxStrategy(string? homeCountry, int rateBasisPoints, bool pricesIncludeTax = false) : ITaxStrategy
{
    public long TaxFor(TaxCalculationInput input)
    {
        if (string.IsNullOrWhiteSpace(homeCountry) || rateBasisPoints <= 0)
        {
            return 0;
        }

        if (!string.Equals(input.ShipCountry, homeCountry, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var taxableSubtotal = input.Lines
            .Where(l => l.TaxMode != TaxMode.Exempt)
            .Sum(l => checked(l.SellingPriceMinor * l.Quantity));
        var totalSubtotal = input.Lines.Sum(l => checked(l.SellingPriceMinor * l.Quantity));
        if (taxableSubtotal == 0 || totalSubtotal == 0)
        {
            return 0;
        }

        var taxableDiscount = input.DiscountMinor * taxableSubtotal / totalSubtotal;
        var taxableBase = Math.Max(0, taxableSubtotal - taxableDiscount);
        return pricesIncludeTax
            ? (long)Math.Round(taxableBase * rateBasisPoints / (10000m + rateBasisPoints), MidpointRounding.ToEven)
            : (long)Math.Round(taxableBase * rateBasisPoints / 10000m, MidpointRounding.ToEven);
    }
}

public sealed class PricingEngine(ITaxStrategy? taxStrategy = null)
{
    private readonly ITaxStrategy _taxStrategy = taxStrategy ?? ZeroTaxStrategy.Instance;

    public PricingResult Price(PricingInput input, IReadOnlyList<Promotion> promotions)
    {
        Validate(input);

        foreach (var promotion in promotions)
        {
            promotion.Validate(input.Currency);
        }

        var subtotal = input.Lines.Sum(l => checked(l.SellingPriceMinor * l.Quantity));

        // ONE promotion decision, shared with checkout (ADR-0051): every promotion that qualifies becomes a
        // PromotionCandidate, and PromotionEvaluator.Select picks the winner(s) — better of
        // [best single Exclusive] vs [Σ all Combinable] by customer benefit — and allocates the discount per
        // line. Threshold promotions (kind 9) get their eligibility AND their reward from the shared
        // evaluator itself; kinds 1-8 keep their engine-only vocabulary here (coupon codes, categories,
        // bundles — none of which exist on the projected PromotionCopy) and hand over a ready-made
        // candidate. Legacy kinds default to Combinable = false, so their selection reduces exactly to the
        // historical "best single promotion wins".
        var promotionLines = input.Lines
            .Select(l => new PromotionLine(l.ProductId, l.SellingPriceMinor, l.Quantity))
            .ToList();
        var candidates = new List<PromotionCandidate>();
        foreach (var promotion in promotions)
        {
            if (ToCandidate(promotion, input, promotionLines, subtotal) is { } candidate)
            {
                candidates.Add(candidate);
            }
        }

        var outcome = PromotionEvaluator.Select(promotionLines, candidates, input.ShippingMinor);

        // Storefront-wide discount (rev_disc): a per-storefront percentage on the items' subtotal only,
        // applied AFTER the effective offer/catalog per-line price is already reflected in SellingPriceMinor.
        // It stacks additively with any promotion discount, and the combined total is capped at the subtotal
        // so the goods can never be discounted below zero (shipping and tax stay outside the base).
        var promotionDiscount = outcome.DiscountMinor;
        var storefrontDiscount = input.StorefrontDiscountBps > 0
            ? (long)Math.Round(subtotal * input.StorefrontDiscountBps / 10000m, MidpointRounding.AwayFromZero)
            : 0;
        var discountMinor = Math.Min(promotionDiscount + storefrontDiscount, subtotal);
        var shippingMinor = outcome.FreeShippingApplied ? 0 : input.ShippingMinor;
        var taxMinor = _taxStrategy.TaxFor(new TaxCalculationInput(input.Currency, input.ShipCountry, input.Lines, discountMinor));
        var gross = subtotal - discountMinor + shippingMinor + taxMinor;

        return new PricingResult(
            subtotal, discountMinor, shippingMinor, taxMinor, gross, input.Currency,
            outcome.AppliedPromotionIds, outcome.FreeShippingApplied, outcome.LineDiscountsMinor);
    }

    private static void Validate(PricingInput input)
    {
        if (input.TenantId == Guid.Empty || input.StorefrontId == Guid.Empty)
        {
            throw new PricingRuleException("Tenant and storefront are required for pricing.");
        }

        if (input.Currency.Length != 3)
        {
            throw new PricingRuleException("Currency must be an ISO 4217 code.");
        }

        if (input.ShippingMinor < 0)
        {
            throw new PricingRuleException("Shipping cannot be negative.");
        }

        if (input.StorefrontDiscountBps is < 0 or > 10000)
        {
            throw new PricingRuleException("Storefront discount basis points must be between 0 and 10000.");
        }

        if (input.Lines.Count == 0)
        {
            throw new PricingRuleException("At least one pricing line is required.");
        }

        foreach (var line in input.Lines)
        {
            if (line.ProductId == Guid.Empty || line.Quantity < 1 || line.SupplierCostMinor < 0 || line.SellingPriceMinor < 0)
            {
                throw new PricingRuleException("Pricing lines require product, positive quantity, and non-negative money values.");
            }
        }
    }

    /// <summary>
    /// Turns one promotion into a candidate for the shared selection step, or null when it does not
    /// qualify. Threshold promotions (ADR-0051) delegate their whole eligibility + reward computation to
    /// <see cref="PromotionEvaluator.CandidateFor"/> — the same code checkout runs. Kinds 1-8 keep their
    /// engine-only eligibility (coupon codes, categories, bundles) and reward here.
    /// </summary>
    private static PromotionCandidate? ToCandidate(
        Promotion promotion, PricingInput input, IReadOnlyList<PromotionLine> lines, long subtotal)
    {
        if (!promotion.Active || promotion.TenantId != input.TenantId)
        {
            return null;
        }

        if (promotion.StorefrontId is { } scopedStorefrontId && scopedStorefrontId != input.StorefrontId)
        {
            return null;
        }

        if (promotion.Kind == PromotionKind.Threshold)
        {
            // No-FX (ADR-0041): a promotion pinned to a currency never applies to another one. An unset
            // currency means the caller did not pin it (engine-only construction) and is not checked.
            if (promotion.Currency.Length > 0
                && !string.Equals(promotion.Currency, input.Currency, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Window bounds are inclusive; they are only checked when the caller supplied an evaluation time.
            if (input.Now is { } now
                && ((promotion.ActiveFrom is { } from && now < from)
                    || (promotion.ActiveUntil is { } until && now > until)))
            {
                return null;
            }

            return PromotionEvaluator.CandidateFor(
                lines,
                promotion.Id,
                promotion.ProductId is null ? PromotionScopeKind.Storefront : PromotionScopeKind.Product,
                promotion.ProductId,
                promotion.MinimumAmountMinor,
                promotion.MinimumQuantity,
                promotion.GrantsFreeShipping,
                promotion.PercentOff,
                promotion.AmountMinor,
                promotion.Combinable);
        }

        if (!IsEligible(promotion, input))
        {
            return null;
        }

        var indexes = EligibleLineIndexes(promotion, input);
        var eligibleSubtotal = indexes.Sum(i => lines[i].TotalMinor);
        var discount = Math.Min(LegacyDiscount(promotion, eligibleSubtotal, subtotal), subtotal);
        return new PromotionCandidate(
            promotion.Id, discount,
            promotion.Kind == PromotionKind.FreeShipping || promotion.GrantsFreeShipping,
            promotion.Combinable, indexes);
    }

    /// <summary>The input line indexes a legacy (kind 1-8) promotion's discount falls on.</summary>
    private static List<int> EligibleLineIndexes(Promotion promotion, PricingInput input)
    {
        var indexes = new List<int>();
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var line = input.Lines[i];
            var matches = promotion.Kind switch
            {
                PromotionKind.AutomaticProduct => line.ProductId == promotion.ProductId,
                PromotionKind.AutomaticCategory => line.CategoryId == promotion.CategoryId,
                PromotionKind.BundleDiscount => line.ProductId == promotion.BundleProductId,
                PromotionKind.QuantityTier => TierLineMatches(promotion, line),
                PromotionKind.FreeShipping => false,
                _ => true,
            };
            if (matches)
            {
                indexes.Add(i);
            }
        }

        return indexes;
    }

    /// <summary>The raw discount a legacy (kind 1-8) promotion takes off, before the subtotal cap.</summary>
    private static long LegacyDiscount(Promotion promotion, long eligibleSubtotal, long subtotal) => promotion.Kind switch
    {
        PromotionKind.CouponFixed => promotion.AmountMinor,
        PromotionKind.CouponPercent => Percent(subtotal, promotion.PercentOff),
        PromotionKind.AutomaticProduct or PromotionKind.AutomaticCategory or PromotionKind.AutomaticStorefront or PromotionKind.BundleDiscount or PromotionKind.QuantityTier =>
            promotion.PercentOff > 0 ? Percent(eligibleSubtotal, promotion.PercentOff) : promotion.AmountMinor,
        _ => 0,
    };

    private static bool IsEligible(Promotion promotion, PricingInput input)
    {
        return promotion.Kind switch
        {
            PromotionKind.CouponFixed or PromotionKind.CouponPercent =>
                !string.IsNullOrWhiteSpace(input.CouponCode) && string.Equals(input.CouponCode, promotion.CouponCode, StringComparison.OrdinalIgnoreCase),
            PromotionKind.AutomaticProduct => input.Lines.Any(l => l.ProductId == promotion.ProductId),
            PromotionKind.AutomaticCategory => input.Lines.Any(l => l.CategoryId == promotion.CategoryId),
            PromotionKind.BundleDiscount => input.Lines.Any(l => l.ProductId == promotion.BundleProductId),
            PromotionKind.QuantityTier => TierEligibleQuantity(promotion, input) >= promotion.MinimumQuantity,
            PromotionKind.AutomaticStorefront or PromotionKind.FreeShipping => true,
            _ => false,
        };
    }

    private static int TierEligibleQuantity(Promotion promotion, PricingInput input) =>
        input.Lines.Where(l => TierLineMatches(promotion, l)).Sum(l => l.Quantity);

    private static bool TierLineMatches(Promotion promotion, PricingLineInput line) =>
        promotion.ProductId is { } productId ? line.ProductId == productId : line.CategoryId == promotion.CategoryId;

    private static long Percent(long amount, int percent) => checked(amount * percent / 100);
}

public sealed class PricingRuleException(string message) : Exception(message);
