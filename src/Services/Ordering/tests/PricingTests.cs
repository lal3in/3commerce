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
    public void Pricing_applies_quantity_tier_promotion_when_threshold_is_met()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var promotion = new Promotion(
            Guid.CreateVersion7(),
            tenantId,
            storefrontId,
            PromotionKind.QuantityTier,
            PercentOff: 15,
            ProductId: productId,
            MinimumQuantity: 3);

        var result = _engine.Price(NewInput(tenantId, storefrontId, lines: [Line(productId, price: 1000, quantity: 3)]), [promotion]);

        Assert.Equal(450, result.DiscountMinor);
        Assert.Equal(promotion.Id, result.AppliedPromotionId);
        Assert.Equal(3049, result.GrossMinor);
    }

    [Fact]
    public void Pricing_ignores_quantity_tier_below_threshold()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var promotion = new Promotion(
            Guid.CreateVersion7(),
            tenantId,
            storefrontId,
            PromotionKind.QuantityTier,
            AmountMinor: 500,
            ProductId: productId,
            MinimumQuantity: 3);

        var result = _engine.Price(NewInput(tenantId, storefrontId, lines: [Line(productId, price: 1000, quantity: 2)]), [promotion]);

        Assert.Equal(0, result.DiscountMinor);
        Assert.Null(result.AppliedPromotionId);
    }

    [Fact]
    public void Pricing_applies_best_discount_wins_between_tier_and_coupon()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();
        var tier = new Promotion(Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.QuantityTier, PercentOff: 10, ProductId: productId, MinimumQuantity: 3);
        var coupon = new Promotion(Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.CouponFixed, AmountMinor: 700, CouponCode: "SAVE");

        var result = _engine.Price(NewInput(tenantId, storefrontId, couponCode: "SAVE", lines: [Line(productId, price: 1000, quantity: 3)]), [tier, coupon]);

        Assert.Equal(700, result.DiscountMinor);
        Assert.Equal(coupon.Id, result.AppliedPromotionId);
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
    public void Tax_defaults_to_zero_without_home_regime()
    {
        var result = _engine.Price(NewInput(lines: [Line(price: 1000)], shipCountry: "AU"), []);

        Assert.Equal(0, result.TaxMinor);
        Assert.Equal(1499, result.GrossMinor);
    }

    [Fact]
    public void Tax_applies_home_regime_rate_for_domestic_shipping()
    {
        var engine = new PricingEngine(new HomeRegimeTaxStrategy("AU", rateBasisPoints: 1000));

        var result = engine.Price(NewInput(lines: [Line(price: 1000)], shipCountry: "AU"), []);

        Assert.Equal(100, result.TaxMinor);
        Assert.Equal(1599, result.GrossMinor);
    }

    [Fact]
    public void Tax_zero_rates_exports_outside_home_regime()
    {
        var engine = new PricingEngine(new HomeRegimeTaxStrategy("AU", rateBasisPoints: 1000));

        var result = engine.Price(NewInput(lines: [Line(price: 1000)], shipCountry: "NZ"), []);

        Assert.Equal(0, result.TaxMinor);
        Assert.Equal(1499, result.GrossMinor);
    }

    [Fact]
    public void Tax_ignores_exempt_lines_and_allocates_discounts()
    {
        var engine = new PricingEngine(new HomeRegimeTaxStrategy("AU", rateBasisPoints: 1000));
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.AutomaticStorefront, AmountMinor: 100);
        var taxable = Line(price: 1000);
        var exempt = Line(price: 1000, taxMode: TaxMode.Exempt);

        var result = engine.Price(NewInput(tenantId, storefrontId, lines: [taxable, exempt], shipCountry: "AU"), [promotion]);

        Assert.Equal(95, result.TaxMinor);
        Assert.Equal(2494, result.GrossMinor);
    }

    [Fact]
    public void Storefront_discount_deducts_from_items_only_not_shipping()
    {
        // 10% storefront discount on a 1000 items subtotal → 100 off the goods; shipping (499) is NOT
        // discounted and no tax applies. Gross = 1000 − 100 + 499 = 1399, and the money identity holds.
        var result = _engine.Price(NewInput(lines: [Line(price: 1000)], storefrontDiscountBps: 1000), []);

        Assert.Equal(1000, result.SubtotalMinor);
        Assert.Equal(100, result.DiscountMinor);
        Assert.Equal(499, result.ShippingMinor);
        Assert.Equal(0, result.TaxMinor);
        Assert.Equal(1399, result.GrossMinor);
        // Net + Ship + Tax = Gross (Net = Subtotal − Discount).
        Assert.Equal(result.GrossMinor, result.SubtotalMinor - result.DiscountMinor + result.ShippingMinor + result.TaxMinor);
    }

    [Fact]
    public void Storefront_discount_applies_on_top_of_the_already_resolved_offer_price()
    {
        // The line's SellingPriceMinor already reflects the effective offer price (600, resolved upstream) —
        // NOT the catalog price. The 10% storefront discount then rides on top of THAT: 60 off 600, so the
        // discount is computed on the offer price the shopper was charged, exactly as checkout does.
        var result = _engine.Price(NewInput(lines: [Line(price: 600)], storefrontDiscountBps: 1000), []);

        Assert.Equal(600, result.SubtotalMinor);
        Assert.Equal(60, result.DiscountMinor);
        Assert.Equal(1039, result.GrossMinor); // 600 − 60 + 499 shipping
    }

    [Fact]
    public void Storefront_discount_stacks_additively_with_a_promotion()
    {
        // A 100 fixed automatic-storefront promotion PLUS a 10% storefront discount on a 1000 subtotal:
        // the two stack additively → 100 + 100 = 200 total off the goods. Gross = 1000 − 200 + 499 = 1299.
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.AutomaticStorefront, AmountMinor: 100);

        var result = _engine.Price(NewInput(tenantId, storefrontId, lines: [Line(price: 1000)], storefrontDiscountBps: 1000), [promotion]);

        Assert.Equal(200, result.DiscountMinor);
        Assert.Equal(1299, result.GrossMinor);
    }

    [Fact]
    public void Combined_discount_is_capped_at_the_subtotal()
    {
        // A 900 fixed coupon PLUS a 50% storefront discount on a 1000 subtotal would total 1400, but the
        // combined discount can never exceed the goods value → capped at 1000. Gross = 0 goods + 499 shipping.
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var coupon = new Promotion(Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.CouponFixed, AmountMinor: 900, CouponCode: "BIG");

        var result = _engine.Price(NewInput(tenantId, storefrontId, couponCode: "BIG", lines: [Line(price: 1000)], storefrontDiscountBps: 5000), [coupon]);

        Assert.Equal(1000, result.DiscountMinor);
        Assert.Equal(499, result.GrossMinor);
    }

    [Fact]
    public void Storefront_discount_taxes_the_discounted_base_for_a_tax_added_regime()
    {
        // Tax-added (US-style exclusive) storefront at 10%: the 10% storefront discount shrinks the taxable
        // base to 900, so tax = 90 (not 100), added on top. Gross = 1000 − 100 discount + 499 ship + 90 tax.
        var engine = new PricingEngine(new HomeRegimeTaxStrategy("AU", rateBasisPoints: 1000));

        var result = engine.Price(NewInput(lines: [Line(price: 1000)], shipCountry: "AU", storefrontDiscountBps: 1000), []);

        Assert.Equal(90, result.TaxMinor);
        Assert.Equal(1489, result.GrossMinor);
    }

    [Fact]
    public void Storefront_discount_taxes_the_discounted_base_for_a_tax_inclusive_regime()
    {
        // Tax-inclusive (AU GST / EU VAT) storefront at 10%: the shelf price already contains the tax. The
        // 10% storefront discount reduces the taxable base to 900, so the CONTAINED tax reported drops to
        // round(900 × 1000 / 11000) = 82 (it is 91 without the discount) — the discount lowers the tax too.
        var engine = new PricingEngine(new HomeRegimeTaxStrategy("AU", rateBasisPoints: 1000, pricesIncludeTax: true));

        var discounted = engine.Price(NewInput(lines: [Line(price: 1000)], shipCountry: "AU", storefrontDiscountBps: 1000), []);
        var undiscounted = engine.Price(NewInput(lines: [Line(price: 1000)], shipCountry: "AU"), []);

        Assert.Equal(82, discounted.TaxMinor);
        Assert.Equal(91, undiscounted.TaxMinor);
    }

    [Fact]
    public void Threshold_promotion_discounts_the_items_only_and_holds_the_money_identity()
    {
        // A storefront-scoped Threshold promotion (ADR-0051): 2 × 6000 = 12000 clears the 10000 money
        // threshold → 10% off the goods = 1200. Shipping (499) is untouched and no tax applies.
        // Gross = 12000 − 1200 + 499 = 11299.
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(
            Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.Threshold,
            PercentOff: 10, MinimumAmountMinor: 10_000, Currency: "AUD");

        var result = _engine.Price(NewInput(tenantId, storefrontId, lines: [Line(price: 6000, quantity: 2)]), [promotion]);

        Assert.Equal(12_000, result.SubtotalMinor);
        Assert.Equal(1_200, result.DiscountMinor);
        Assert.Equal(499, result.ShippingMinor);
        Assert.Equal(11_299, result.GrossMinor);
        Assert.Equal([promotion.Id], result.AppliedPromotionIds);
        // Net + Ship + Tax = Gross (Net = Subtotal − Discount).
        Assert.Equal(result.GrossMinor, result.SubtotalMinor - result.DiscountMinor + result.ShippingMinor + result.TaxMinor);
    }

    [Fact]
    public void Threshold_promotion_below_the_threshold_changes_nothing()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(
            Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.Threshold,
            PercentOff: 10, MinimumAmountMinor: 10_000, Currency: "AUD");

        var result = _engine.Price(NewInput(tenantId, storefrontId, lines: [Line(price: 6000)]), [promotion]);

        Assert.Equal(0, result.DiscountMinor);
        Assert.Null(result.AppliedPromotionId);
        Assert.Equal(6_499, result.GrossMinor);
    }

    [Fact]
    public void Threshold_free_shipping_promotion_zeroes_shipping()
    {
        // Free shipping only: no discount on the goods, shipping drops from 499 to 0.
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(
            Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.Threshold,
            MinimumAmountMinor: 10_000, GrantsFreeShipping: true, Currency: "AUD");

        var result = _engine.Price(NewInput(tenantId, storefrontId, lines: [Line(price: 12_000)]), [promotion]);

        Assert.True(result.FreeShippingApplied);
        Assert.Equal(0, result.ShippingMinor);
        Assert.Equal(0, result.DiscountMinor);
        Assert.Equal(12_000, result.GrossMinor);
        Assert.Equal(result.GrossMinor, result.SubtotalMinor - result.DiscountMinor + result.ShippingMinor + result.TaxMinor);
    }

    [Fact]
    public void Threshold_promotion_stacks_additively_with_the_storefront_wide_discount()
    {
        // A 10% Threshold promotion (1000 off a 10000 subtotal) PLUS a 10% storefront-wide discount
        // (another 1000): the two stack additively → 2000 total off the goods. The storefront discount is
        // a store setting, NOT a promotion — the combinable flag never governs it (ADR-0051).
        // Gross = 10000 − 2000 + 499 = 8499.
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(
            Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.Threshold,
            PercentOff: 10, MinimumAmountMinor: 5_000, Currency: "AUD");

        var result = _engine.Price(
            NewInput(tenantId, storefrontId, lines: [Line(price: 10_000)], storefrontDiscountBps: 1000), [promotion]);

        Assert.Equal(2_000, result.DiscountMinor);
        Assert.Equal(8_499, result.GrossMinor);
        Assert.Equal(result.GrossMinor, result.SubtotalMinor - result.DiscountMinor + result.ShippingMinor + result.TaxMinor);
    }

    [Fact]
    public void Threshold_promotion_plus_storefront_discount_is_capped_at_the_subtotal()
    {
        // A 90%-off Threshold promotion PLUS a 50% storefront discount would total 140% of the goods; the
        // pair is capped at the subtotal so the goods land at zero and the gross is shipping only.
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(
            Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.Threshold,
            PercentOff: 90, MinimumAmountMinor: 500, Currency: "AUD");

        var result = _engine.Price(
            NewInput(tenantId, storefrontId, lines: [Line(price: 1000)], storefrontDiscountBps: 5000), [promotion]);

        Assert.Equal(1_000, result.DiscountMinor);
        Assert.Equal(499, result.GrossMinor);
    }

    [Fact]
    public void Threshold_discount_shrinks_the_taxable_base_in_a_tax_added_regime()
    {
        // Tax-added (US-style exclusive) at 10%: a 10% Threshold discount shrinks the taxable base to 900,
        // so tax = 90 (not 100). Gross = 1000 − 100 + 499 ship + 90 tax = 1489.
        var engine = new PricingEngine(new HomeRegimeTaxStrategy("AU", rateBasisPoints: 1000));
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(
            Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.Threshold,
            PercentOff: 10, MinimumAmountMinor: 500, Currency: "AUD");

        var result = engine.Price(
            NewInput(tenantId, storefrontId, lines: [Line(price: 1000)], shipCountry: "AU"), [promotion]);

        Assert.Equal(100, result.DiscountMinor);
        Assert.Equal(90, result.TaxMinor);
        Assert.Equal(1_489, result.GrossMinor);
        Assert.Equal(result.GrossMinor, result.SubtotalMinor - result.DiscountMinor + result.ShippingMinor + result.TaxMinor);
    }

    [Fact]
    public void Threshold_discount_shrinks_the_taxable_base_in_a_tax_inclusive_regime()
    {
        // Tax-inclusive (AU GST / EU VAT) at 10%: the shelf price already contains the tax. A 10%
        // Threshold discount reduces the taxable base to 900, so the CONTAINED tax reported drops from
        // round(1000 × 1000 / 11000) = 91 to round(900 × 1000 / 11000) = 82.
        var engine = new PricingEngine(new HomeRegimeTaxStrategy("AU", rateBasisPoints: 1000, pricesIncludeTax: true));
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(
            Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.Threshold,
            PercentOff: 10, MinimumAmountMinor: 500, Currency: "AUD");

        var discounted = engine.Price(
            NewInput(tenantId, storefrontId, lines: [Line(price: 1000)], shipCountry: "AU"), [promotion]);
        var undiscounted = engine.Price(NewInput(lines: [Line(price: 1000)], shipCountry: "AU"), []);

        Assert.Equal(82, discounted.TaxMinor);
        Assert.Equal(91, undiscounted.TaxMinor);
    }

    [Fact]
    public void Threshold_promotions_stack_when_combinable_and_report_every_applied_id()
    {
        // Two combinable 10% promotions beat a single 15% exclusive (2000 vs 1500), and BOTH ids are
        // reported; AppliedPromotionId still returns the first for callers that predate stacking.
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var exclusive = new Promotion(
            new Guid("00000000-0000-0000-0000-0000000000c1"), tenantId, storefrontId, PromotionKind.Threshold,
            PercentOff: 15, MinimumAmountMinor: 500, Currency: "AUD");
        var first = new Promotion(
            new Guid("00000000-0000-0000-0000-0000000000c2"), tenantId, storefrontId, PromotionKind.Threshold,
            PercentOff: 10, MinimumAmountMinor: 500, Combinable: true, Currency: "AUD");
        var second = new Promotion(
            new Guid("00000000-0000-0000-0000-0000000000c3"), tenantId, storefrontId, PromotionKind.Threshold,
            PercentOff: 10, MinimumAmountMinor: 500, Combinable: true, Currency: "AUD");

        var result = _engine.Price(
            NewInput(tenantId, storefrontId, lines: [Line(price: 10_000)]), [exclusive, first, second]);

        Assert.Equal(2_000, result.DiscountMinor);
        Assert.Equal(2, result.AppliedPromotionIds.Count);
        Assert.Equal([first.Id, second.Id], result.AppliedPromotionIds);
        Assert.Equal(first.Id, result.AppliedPromotionId);
        Assert.Equal(2_000, result.LinePromotionDiscountsMinor.Sum());
    }

    [Fact]
    public void Threshold_promotion_in_another_currency_never_applies()
    {
        // No FX (ADR-0041): a EUR promotion on an AUD cart is ineligible.
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var promotion = new Promotion(
            Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.Threshold,
            PercentOff: 50, MinimumAmountMinor: 500, Currency: "EUR");

        var result = _engine.Price(NewInput(tenantId, storefrontId, lines: [Line(price: 10_000)]), [promotion]);

        Assert.Equal(0, result.DiscountMinor);
    }

    [Fact]
    public void Threshold_promotion_outside_its_window_never_applies()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var expired = new Promotion(
            Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.Threshold,
            PercentOff: 50, MinimumAmountMinor: 500, Currency: "AUD", ActiveUntil: now.AddDays(-1));

        var result = _engine.Price(
            NewInput(tenantId, storefrontId, lines: [Line(price: 10_000)], now: now), [expired]);

        Assert.Equal(0, result.DiscountMinor);
    }

    [Fact]
    public void Threshold_promotion_without_a_threshold_or_a_reward_is_rejected()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var noThreshold = new Promotion(
            Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.Threshold, PercentOff: 10, Currency: "AUD");
        var noReward = new Promotion(
            Guid.CreateVersion7(), tenantId, storefrontId, PromotionKind.Threshold, MinimumAmountMinor: 500, Currency: "AUD");

        var thresholdEx = Assert.Throws<PricingRuleException>(
            () => _engine.Price(NewInput(tenantId, storefrontId), [noThreshold]));
        var rewardEx = Assert.Throws<PricingRuleException>(
            () => _engine.Price(NewInput(tenantId, storefrontId), [noReward]));

        Assert.Contains("minimum amount or a minimum quantity", thresholdEx.Message, StringComparison.Ordinal);
        Assert.Contains("require a reward", rewardEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pricing_rejects_negative_money()
    {
        var ex = Assert.Throws<PricingRuleException>(() => _engine.Price(NewInput(lines: [Line(price: -1)]), []));

        Assert.Contains("non-negative money", ex.Message, StringComparison.Ordinal);
    }

    private static PricingInput NewInput(Guid? tenantId = null, Guid? storefrontId = null, string? couponCode = null, IReadOnlyList<PricingLineInput>? lines = null, string? shipCountry = null, int storefrontDiscountBps = 0, DateTimeOffset? now = null) => new(
        tenantId ?? Guid.CreateVersion7(),
        storefrontId ?? Guid.CreateVersion7(),
        "AUD",
        lines ?? [Line(price: 1000)],
        499,
        couponCode,
        shipCountry,
        storefrontDiscountBps,
        now);

    private static PricingLineInput Line(Guid? productId = null, long price = 1000, int quantity = 1, Guid? categoryId = null, TaxMode taxMode = TaxMode.Exclusive) => new(
        productId ?? Guid.CreateVersion7(),
        categoryId,
        Guid.CreateVersion7(),
        SupplierCostMinor: Math.Max(0, price / 2),
        SellingPriceMinor: price,
        Quantity: quantity,
        TaxMode: taxMode);
}
