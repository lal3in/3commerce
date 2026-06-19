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
}

public sealed record PricingInput(
    Guid TenantId,
    Guid StorefrontId,
    string Currency,
    IReadOnlyList<PricingLineInput> Lines,
    long ShippingMinor,
    string? CouponCode = null);

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
    bool Active = true)
{
    public void Validate(string currency)
    {
        if (TenantId == Guid.Empty)
        {
            throw new PricingRuleException("Promotion tenant is required.");
        }

        if (AmountMinor < 0 || PercentOff is < 0 or > 100)
        {
            throw new PricingRuleException("Promotion discount values are invalid.");
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
    Guid? AppliedPromotionId,
    bool FreeShippingApplied);

public sealed class PricingEngine
{
    public PricingResult Price(PricingInput input, IReadOnlyList<Promotion> promotions)
    {
        Validate(input);

        foreach (var promotion in promotions)
        {
            promotion.Validate(input.Currency);
        }

        var subtotal = input.Lines.Sum(l => checked(l.SellingPriceMinor * l.Quantity));
        var best = promotions
            .Where(p => IsEligible(p, input))
            .Select(p => Evaluate(p, input, subtotal))
            .OrderByDescending(p => p.DiscountMinor + (p.FreeShippingApplied ? input.ShippingMinor : 0))
            .ThenBy(p => p.PromotionId)
            .FirstOrDefault();

        var discountMinor = Math.Min(best?.DiscountMinor ?? 0, subtotal);
        var shippingMinor = best?.FreeShippingApplied == true ? 0 : input.ShippingMinor;
        var taxMinor = 0L; // mt3_15 wires real ITaxStrategy; mt3_4 records mode and keeps seam explicit.
        var gross = subtotal - discountMinor + shippingMinor + taxMinor;

        return new PricingResult(subtotal, discountMinor, shippingMinor, taxMinor, gross, input.Currency, best?.PromotionId, best?.FreeShippingApplied == true);
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

    private static bool IsEligible(Promotion promotion, PricingInput input)
    {
        if (!promotion.Active || promotion.TenantId != input.TenantId)
        {
            return false;
        }

        if (promotion.StorefrontId is { } storefrontId && storefrontId != input.StorefrontId)
        {
            return false;
        }

        return promotion.Kind switch
        {
            PromotionKind.CouponFixed or PromotionKind.CouponPercent =>
                !string.IsNullOrWhiteSpace(input.CouponCode) && string.Equals(input.CouponCode, promotion.CouponCode, StringComparison.OrdinalIgnoreCase),
            PromotionKind.AutomaticProduct => input.Lines.Any(l => l.ProductId == promotion.ProductId),
            PromotionKind.AutomaticCategory => input.Lines.Any(l => l.CategoryId == promotion.CategoryId),
            PromotionKind.BundleDiscount => input.Lines.Any(l => l.ProductId == promotion.BundleProductId),
            PromotionKind.AutomaticStorefront or PromotionKind.FreeShipping => true,
            _ => false,
        };
    }

    private static PromotionEvaluation Evaluate(Promotion promotion, PricingInput input, long subtotal)
    {
        var eligibleSubtotal = promotion.Kind switch
        {
            PromotionKind.AutomaticProduct => input.Lines.Where(l => l.ProductId == promotion.ProductId).Sum(LineTotal),
            PromotionKind.AutomaticCategory => input.Lines.Where(l => l.CategoryId == promotion.CategoryId).Sum(LineTotal),
            PromotionKind.BundleDiscount => input.Lines.Where(l => l.ProductId == promotion.BundleProductId).Sum(LineTotal),
            PromotionKind.FreeShipping => 0,
            _ => subtotal,
        };

        var discount = promotion.Kind switch
        {
            PromotionKind.CouponFixed => promotion.AmountMinor,
            PromotionKind.CouponPercent => Percent(subtotal, promotion.PercentOff),
            PromotionKind.AutomaticProduct or PromotionKind.AutomaticCategory or PromotionKind.AutomaticStorefront or PromotionKind.BundleDiscount =>
                promotion.PercentOff > 0 ? Percent(eligibleSubtotal, promotion.PercentOff) : promotion.AmountMinor,
            PromotionKind.FreeShipping => 0,
            _ => 0,
        };

        return new PromotionEvaluation(promotion.Id, Math.Min(discount, subtotal), promotion.Kind == PromotionKind.FreeShipping);
    }

    private static long LineTotal(PricingLineInput line) => checked(line.SellingPriceMinor * line.Quantity);

    private static long Percent(long amount, int percent) => checked(amount * percent / 100);

    private sealed record PromotionEvaluation(Guid PromotionId, long DiscountMinor, bool FreeShippingApplied);
}

public sealed class PricingRuleException(string message) : Exception(message);
