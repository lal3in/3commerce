using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

public class PricingTests
{
    private readonly PricingEngine _engine = new();

    [Fact]
    public void Pricing_calculates_selling_price_totals_without_floats()
    {
        var input = NewInput(lines: [Line(price: 1299, quantity: 2)]);

        var result = _engine.Price(input, []);

        Assert.Equal(2598, result.SubtotalMinor);
        Assert.Equal(499, result.ShippingMinor);
        Assert.Equal(3097, result.GrossMinor);
        Assert.Equal("AUD", result.Currency);
    }

    [Fact]
    public void Pricing_applies_matching_fixed_coupon()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.CouponFixed, AmountMinor: 500, CouponCode: "SAVE500");

        var result = _engine.Price(NewInput(tenantId, storefrontId, couponCode: "save500", lines: [Line(price: 2000)]), [promotion]);

        Assert.Equal(500, result.DiscountMinor);
        Assert.Equal(promotion.Id, result.AppliedPromotionId);
        Assert.Equal(1999, result.GrossMinor);
    }

    [Fact]
    public void Pricing_applies_best_discount_wins()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var small = new Promotion(Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.AutomaticProduct, PercentOff: 10, ProductId: productId);
        var best = new Promotion(Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.CouponPercent, PercentOff: 25, CouponCode: "BEST");

        var result = _engine.Price(NewInput(tenantId, storefrontId, couponCode: "BEST", lines: [Line(productId, price: 4000)]), [small, best]);

        Assert.Equal(1000, result.DiscountMinor);
        Assert.Equal(best.Id, result.AppliedPromotionId);
    }

    [Fact]
    public void Pricing_supports_category_automatic_promotion()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var categoryId = Guid.CreateVersion7();
        var promotion = new Promotion(Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.AutomaticCategory, PercentOff: 20, CategoryId: categoryId);

        var result = _engine.Price(NewInput(tenantId, storefrontId, lines: [Line(price: 5000, categoryId: categoryId), Line(price: 3000)]), [promotion]);

        Assert.Equal(1000, result.DiscountMinor);
        Assert.Equal(7499, result.GrossMinor);
    }

    [Fact]
    public void Pricing_supports_free_shipping_promotion()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.FreeShipping);

        var result = _engine.Price(NewInput(tenantId, storefrontId, lines: [Line(price: 1000)]), [promotion]);

        Assert.True(result.FreeShippingApplied);
        Assert.Equal(0, result.ShippingMinor);
        Assert.Equal(1000, result.GrossMinor);
    }

    [Fact]
    public void Pricing_ignores_other_storefront_promotions()
    {
        var tenantId = Guid.CreateVersion7();
        var inputStorefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(Guid.CreateVersion7(), tenantId, Guid.CreateVersion7(), PromotionKind.AutomaticStorefront, PercentOff: 90);

        var result = _engine.Price(NewInput(tenantId, inputStorefrontId, lines: [Line(price: 1000)]), [promotion]);

        Assert.Equal(0, result.DiscountMinor);
    }

    [Fact]
    public void Pricing_rejects_negative_money()
    {
        var ex = Assert.Throws<PricingRuleException>(() => _engine.Price(NewInput(lines: [Line(price: -1)]), []));

        Assert.Contains("non-negative money", ex.Message, StringComparison.Ordinal);
    }

    private static PricingInput NewInput(Guid? tenantId = null, Guid? storefrontId = null, string? couponCode = null, IReadOnlyList<PricingLineInput>? lines = null) => new(
        tenantId ?? Guid.CreateVersion7(),
        storefrontId ?? Guid.CreateVersion7(),
        "AUD",
        lines ?? [Line(price: 1000)],
        499,
        couponCode);

    private static PricingLineInput Line(Guid? productId = null, long price = 1000, int quantity = 1, Guid? categoryId = null) => new(
        productId ?? Guid.CreateVersion7(),
        categoryId,
        Guid.CreateVersion7(),
        SupplierCostMinor: Math.Max(0, price / 2),
        SellingPriceMinor: price,
        Quantity: quantity,
        TaxMode: TaxMode.Exclusive);
}
