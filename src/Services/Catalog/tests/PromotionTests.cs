using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Tests;

/// <summary>
/// The threshold-promotion aggregate (ADR-0051): a promotion needs at least one threshold (money and/or
/// quantity — both set are ANDed at evaluation time in Ordering) and at least one reward (free shipping,
/// a percentage, or a fixed minor-unit amount — percent and fixed are mutually exclusive). Scope and
/// ProductId are bound together, the active window is inclusive and ordered, and only Active promotions
/// are ever effective.
/// </summary>
public class PromotionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static Promotion NewStorefrontPromotion() =>
        Promotion.Create(Tenant, "Spend 100 ship free", "eur", PromotionScope.Storefront, null, Now);

    private static Promotion NewProductPromotion(Guid productId) =>
        Promotion.Create(Tenant, "Buy 3 get 15% off", "AUD", PromotionScope.Product, productId, Now);

    [Fact]
    public void Create_normalizes_the_currency_and_trims_the_name()
    {
        var promotion = Promotion.Create(Tenant, "  Spring sale  ", "aud", PromotionScope.Storefront, null, Now);

        Assert.Equal("AUD", promotion.Currency);
        Assert.Equal("Spring sale", promotion.Name);
        Assert.Equal(PromotionScope.Storefront, promotion.Scope);
        Assert.Null(promotion.ProductId);
        Assert.True(promotion.IsActive);
    }

    [Fact]
    public void A_money_threshold_with_free_shipping_is_valid()
    {
        var promotion = NewStorefrontPromotion();

        promotion.SetThreshold(10_000, 0, Now);
        promotion.SetReward(grantsFreeShipping: true, percentOff: 0, discountAmountMinor: 0, Now);

        Assert.Equal(10_000, promotion.MinimumAmountMinor);
        Assert.Equal(0, promotion.MinimumQuantity);
        Assert.True(promotion.GrantsFreeShipping);
        Assert.Equal(0, promotion.PercentOff);
        Assert.Equal(0, promotion.DiscountAmountMinor);
    }

    [Fact]
    public void A_quantity_threshold_with_a_percentage_is_valid()
    {
        var promotion = NewProductPromotion(Guid.CreateVersion7());

        promotion.SetThreshold(0, 3, Now);
        promotion.SetReward(grantsFreeShipping: false, percentOff: 15, discountAmountMinor: 0, Now);

        Assert.Equal(0, promotion.MinimumAmountMinor);
        Assert.Equal(3, promotion.MinimumQuantity);
        Assert.Equal(15, promotion.PercentOff);
    }

    [Fact]
    public void Both_thresholds_are_persisted_together_and_ANDed_at_evaluation()
    {
        // The aggregate simply stores both bounds; ANDing them is Ordering's PromotionEvaluator job
        // (ADR-0051 decision 3). What matters here is that setting both keeps both.
        var promotion = NewStorefrontPromotion();

        promotion.SetThreshold(20_000, 5, Now);
        promotion.SetReward(grantsFreeShipping: false, percentOff: 0, discountAmountMinor: 2_000, Now);

        Assert.Equal(20_000, promotion.MinimumAmountMinor);
        Assert.Equal(5, promotion.MinimumQuantity);
        Assert.Equal(2_000, promotion.DiscountAmountMinor);
    }

    [Fact]
    public void A_promotion_with_no_threshold_is_rejected()
    {
        var promotion = NewStorefrontPromotion();

        var ex = Assert.Throws<CatalogRuleException>(() => promotion.SetThreshold(0, 0, Now));

        Assert.Contains("minimum amount or a minimum quantity", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_promotion_with_no_reward_is_rejected()
    {
        var promotion = NewStorefrontPromotion();

        var ex = Assert.Throws<CatalogRuleException>(
            () => promotion.SetReward(grantsFreeShipping: false, percentOff: 0, discountAmountMinor: 0, Now));

        Assert.Contains("requires a reward", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_percentage_and_a_fixed_amount_together_are_rejected()
    {
        var promotion = NewStorefrontPromotion();

        var ex = Assert.Throws<CatalogRuleException>(
            () => promotion.SetReward(grantsFreeShipping: false, percentOff: 10, discountAmountMinor: 500, Now));

        Assert.Contains("not both", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_percentage_above_one_hundred_is_rejected()
    {
        var promotion = NewStorefrontPromotion();

        var ex = Assert.Throws<CatalogRuleException>(
            () => promotion.SetReward(grantsFreeShipping: false, percentOff: 101, discountAmountMinor: 0, Now));

        Assert.Contains("between 0 and 100", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_threshold_is_rejected()
    {
        var promotion = NewStorefrontPromotion();

        Assert.Throws<CatalogRuleException>(() => promotion.SetThreshold(-1, 0, Now));
        Assert.Throws<CatalogRuleException>(() => promotion.SetThreshold(0, -1, Now));
    }

    [Fact]
    public void A_product_scoped_promotion_without_a_product_is_rejected()
    {
        var ex = Assert.Throws<CatalogRuleException>(
            () => Promotion.Create(Tenant, "Bad", "EUR", PromotionScope.Product, null, Now));

        Assert.Contains("requires a product", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_storefront_scoped_promotion_with_a_product_is_rejected()
    {
        var ex = Assert.Throws<CatalogRuleException>(
            () => Promotion.Create(Tenant, "Bad", "EUR", PromotionScope.Storefront, Guid.CreateVersion7(), Now));

        Assert.Contains("must not target a product", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_tenant_name_or_currency_is_rejected()
    {
        Assert.Throws<CatalogRuleException>(
            () => Promotion.Create(Guid.Empty, "Name", "EUR", PromotionScope.Storefront, null, Now));
        Assert.Throws<CatalogRuleException>(
            () => Promotion.Create(Tenant, "   ", "EUR", PromotionScope.Storefront, null, Now));
        Assert.Throws<CatalogRuleException>(
            () => Promotion.Create(Tenant, "Name", "EU", PromotionScope.Storefront, null, Now));
    }

    [Fact]
    public void An_active_from_after_active_until_is_rejected()
    {
        var promotion = NewStorefrontPromotion();

        var ex = Assert.Throws<CatalogRuleException>(
            () => promotion.SetActiveWindow(Now.AddDays(5), Now.AddDays(1), Now));

        Assert.Contains("must not be after", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsEffectiveAt_is_true_inside_the_window_and_false_outside()
    {
        var storefrontId = Guid.CreateVersion7();
        var promotion = NewStorefrontPromotion();
        promotion.SetThreshold(10_000, 0, Now);
        promotion.SetReward(grantsFreeShipping: true, percentOff: 0, discountAmountMinor: 0, Now);
        promotion.SetActiveWindow(Now, Now.AddDays(7), Now);

        // Bounds are inclusive at both ends.
        Assert.True(promotion.IsEffectiveAt(Now, storefrontId));
        Assert.True(promotion.IsEffectiveAt(Now.AddDays(7), storefrontId));
        Assert.False(promotion.IsEffectiveAt(Now.AddSeconds(-1), storefrontId));
        Assert.False(promotion.IsEffectiveAt(Now.AddDays(7).AddSeconds(1), storefrontId));
    }

    [Fact]
    public void IsEffectiveAt_is_false_for_another_storefront_and_true_when_unscoped()
    {
        var mine = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var promotion = NewStorefrontPromotion();
        promotion.SetThreshold(10_000, 0, Now);
        promotion.SetReward(grantsFreeShipping: true, percentOff: 0, discountAmountMinor: 0, Now);

        // Null scope = every storefront of the promotion's currency.
        Assert.True(promotion.IsEffectiveAt(Now, other));

        promotion.SetStorefront(mine, Now);
        Assert.True(promotion.IsEffectiveAt(Now, mine));
        Assert.False(promotion.IsEffectiveAt(Now, other));
    }

    [Fact]
    public void Deactivate_and_Activate_round_trip()
    {
        var storefrontId = Guid.CreateVersion7();
        var promotion = NewStorefrontPromotion();
        promotion.SetThreshold(10_000, 0, Now);
        promotion.SetReward(grantsFreeShipping: true, percentOff: 0, discountAmountMinor: 0, Now);

        promotion.Deactivate(Now);
        Assert.False(promotion.IsActive);
        Assert.False(promotion.IsEffectiveAt(Now, storefrontId));

        promotion.Activate(Now);
        Assert.True(promotion.IsActive);
        Assert.True(promotion.IsEffectiveAt(Now, storefrontId));
    }

    [Fact]
    public void SetCombinable_and_Rename_round_trip()
    {
        var promotion = NewStorefrontPromotion();

        Assert.False(promotion.Combinable);
        promotion.SetCombinable(true, Now);
        Assert.True(promotion.Combinable);

        promotion.Rename("  Autumn sale  ", Now);
        Assert.Equal("Autumn sale", promotion.Name);
        Assert.Throws<CatalogRuleException>(() => promotion.Rename("  ", Now));
    }
}
