using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// The shared promotion decision (ADR-0051) that BOTH PricingEngine and checkout run: eligibility →
/// threshold measurement on the promotion's own scope base → reward → combinability selection →
/// per-line allocation. Everything here is pure — plain PromotionCopy objects, no EF, no clock.
/// </summary>
public class PromotionEvaluatorTests
{
    private static readonly Guid Tenant = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Storefront = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProductA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ProductB = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    // Fixed, ordered ids so the ascending-PromotionId tiebreak is deterministic.
    private static readonly Guid P1 = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid P2 = new("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid P3 = new("00000000-0000-0000-0000-0000000000a3");

    [Fact]
    public void A_money_threshold_that_is_met_discounts_the_cart()
    {
        // Cart: 2 × 6000 = 12000, threshold 10000 met → 10% off = 1200 spread across the single line.
        var lines = new[] { Line(ProductA, 6_000, 2) };
        var promotion = Promo(P1, minimumAmountMinor: 10_000, percentOff: 10);

        var outcome = PromotionEvaluator.Evaluate(lines, [promotion], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(1_200, outcome.DiscountMinor);
        Assert.Equal([P1], outcome.AppliedPromotionIds);
        Assert.Equal(1_200, outcome.LineDiscountsMinor.Sum());
    }

    [Fact]
    public void A_money_threshold_that_is_not_met_grants_nothing()
    {
        var lines = new[] { Line(ProductA, 6_000, 1) };
        var promotion = Promo(P1, minimumAmountMinor: 10_000, percentOff: 10);

        var outcome = PromotionEvaluator.Evaluate(lines, [promotion], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(0, outcome.DiscountMinor);
        Assert.Empty(outcome.AppliedPromotionIds);
    }

    [Fact]
    public void A_threshold_met_exactly_qualifies()
    {
        // The comparison is >=, not >: a cart landing exactly on the threshold wins.
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotion = Promo(P1, minimumAmountMinor: 10_000, discountAmountMinor: 500);

        var outcome = PromotionEvaluator.Evaluate(lines, [promotion], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(500, outcome.DiscountMinor);
    }

    [Fact]
    public void A_quantity_threshold_is_measured_on_unit_count()
    {
        var lines = new[] { Line(ProductA, 1_000, 3) };

        var met = PromotionEvaluator.Evaluate(
            lines, [Promo(P1, minimumQuantity: 3, percentOff: 15)], Tenant, Storefront, "AUD", 499, Now);
        var short_ = PromotionEvaluator.Evaluate(
            [Line(ProductA, 1_000, 2)], [Promo(P1, minimumQuantity: 3, percentOff: 15)], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(450, met.DiscountMinor);
        Assert.Equal(0, short_.DiscountMinor);
    }

    [Fact]
    public void Both_thresholds_set_are_ANDed()
    {
        // 5 × 3000 = 15000 clears the 10000 money threshold, but 5 units is short of the 6-unit threshold —
        // both must be met (ADR-0051 decision 3), so nothing applies.
        var lines = new[] { Line(ProductA, 3_000, 5) };
        var promotion = Promo(P1, minimumAmountMinor: 10_000, minimumQuantity: 6, percentOff: 10);

        var outcome = PromotionEvaluator.Evaluate(lines, [promotion], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(0, outcome.DiscountMinor);

        // One more unit clears both: 6 × 3000 = 18000 → 10% = 1800.
        var cleared = PromotionEvaluator.Evaluate(
            [Line(ProductA, 3_000, 6)], [promotion], Tenant, Storefront, "AUD", 499, Now);
        Assert.Equal(1_800, cleared.DiscountMinor);
    }

    [Fact]
    public void Product_scope_measures_only_that_products_value_and_quantity()
    {
        // Cart: A = 2000, B = 20000. A product-A promotion with a 5000 money threshold must NOT fire even
        // though the whole cart is far above it — the scope base is A's 2000.
        var lines = new[] { Line(ProductA, 1_000, 2), Line(ProductB, 10_000, 2) };
        var promotion = Promo(P1, scope: PromotionScopeKind.Product, productId: ProductA, minimumAmountMinor: 5_000, percentOff: 10);

        var outcome = PromotionEvaluator.Evaluate(lines, [promotion], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(0, outcome.DiscountMinor);
    }

    [Fact]
    public void Product_scope_sums_across_every_line_of_that_product_and_discounts_only_those_lines()
    {
        // Product A appears on two variant lines (1500 + 2500 = 4000, 4 units); product B is untouched.
        var lines = new[] { Line(ProductA, 1_500, 1), Line(ProductB, 9_000, 1), Line(ProductA, 2_500, 1) };
        var promotion = Promo(P1, scope: PromotionScopeKind.Product, productId: ProductA, minimumAmountMinor: 4_000, percentOff: 10);

        var outcome = PromotionEvaluator.Evaluate(lines, [promotion], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(400, outcome.DiscountMinor);
        Assert.Equal(0, outcome.LineDiscountsMinor[1]); // product B keeps its full price
        Assert.Equal(400, outcome.LineDiscountsMinor[0] + outcome.LineDiscountsMinor[2]);
    }

    [Fact]
    public void A_product_scoped_promotion_whose_product_is_absent_is_ineligible()
    {
        var lines = new[] { Line(ProductB, 10_000, 1) };
        var promotion = Promo(P1, scope: PromotionScopeKind.Product, productId: ProductA, minimumAmountMinor: 1_000, percentOff: 50);

        var outcome = PromotionEvaluator.Evaluate(lines, [promotion], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(0, outcome.DiscountMinor);
        Assert.Empty(outcome.AppliedPromotionIds);
    }

    [Fact]
    public void A_fixed_amount_is_clamped_to_its_own_scope_base()
    {
        // A 5000-off promotion scoped to a 2000 product takes 2000, never more (the goods can't go negative).
        var lines = new[] { Line(ProductA, 2_000, 1), Line(ProductB, 9_000, 1) };
        var promotion = Promo(P1, scope: PromotionScopeKind.Product, productId: ProductA, minimumAmountMinor: 1_000, discountAmountMinor: 5_000);

        var outcome = PromotionEvaluator.Evaluate(lines, [promotion], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(2_000, outcome.DiscountMinor);
        Assert.Equal(2_000, outcome.LineDiscountsMinor[0]);
        Assert.Equal(0, outcome.LineDiscountsMinor[1]);
    }

    [Fact]
    public void A_free_shipping_only_promotion_grants_shipping_and_no_discount()
    {
        var lines = new[] { Line(ProductA, 12_000, 1) };
        var promotion = Promo(P1, minimumAmountMinor: 10_000, grantsFreeShipping: true);

        var outcome = PromotionEvaluator.Evaluate(lines, [promotion], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(0, outcome.DiscountMinor);
        Assert.True(outcome.FreeShippingApplied);
        Assert.Equal([P1], outcome.AppliedPromotionIds);
    }

    [Fact]
    public void An_exclusive_beats_a_smaller_combinable_pair()
    {
        // Exclusive 20% off 10000 = 2000 vs two combinables of 5% + 5% = 1000. Exclusive wins alone.
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotions = new[]
        {
            Promo(P1, minimumAmountMinor: 1_000, percentOff: 20),
            Promo(P2, minimumAmountMinor: 1_000, percentOff: 5, combinable: true),
            Promo(P3, minimumAmountMinor: 1_000, percentOff: 5, combinable: true),
        };

        var outcome = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(2_000, outcome.DiscountMinor);
        Assert.Equal([P1], outcome.AppliedPromotionIds);
    }

    [Fact]
    public void Two_combinables_beat_a_bigger_single_exclusive()
    {
        // Exclusive 15% = 1500 vs combinables 10% + 10% = 2000 → the stack wins and BOTH ids are reported.
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotions = new[]
        {
            Promo(P1, minimumAmountMinor: 1_000, percentOff: 15),
            Promo(P2, minimumAmountMinor: 1_000, percentOff: 10, combinable: true),
            Promo(P3, minimumAmountMinor: 1_000, percentOff: 10, combinable: true),
        };

        var outcome = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(2_000, outcome.DiscountMinor);
        Assert.Equal([P2, P3], outcome.AppliedPromotionIds);
    }

    [Fact]
    public void An_exact_tie_goes_to_the_combinable_set()
    {
        // Exclusive 20% = 2000 vs combinables 10% + 10% = 2000. Same benefit → the combinable set wins,
        // because it shows the shopper more applied promotions for the same money (ADR-0051 decision 4).
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotions = new[]
        {
            Promo(P1, minimumAmountMinor: 1_000, percentOff: 20),
            Promo(P2, minimumAmountMinor: 1_000, percentOff: 10, combinable: true),
            Promo(P3, minimumAmountMinor: 1_000, percentOff: 10, combinable: true),
        };

        var outcome = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(2_000, outcome.DiscountMinor);
        Assert.Equal(2, outcome.AppliedPromotionIds.Count);
        Assert.Equal([P2, P3], outcome.AppliedPromotionIds);
    }

    [Fact]
    public void An_exclusive_tie_breaks_on_the_ascending_promotion_id()
    {
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotions = new[]
        {
            Promo(P2, minimumAmountMinor: 1_000, percentOff: 10),
            Promo(P1, minimumAmountMinor: 1_000, percentOff: 10),
        };

        var outcome = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal([P1], outcome.AppliedPromotionIds);
    }

    [Fact]
    public void Free_shipping_value_counts_toward_the_customer_benefit()
    {
        // A free-shipping exclusive (benefit = 2000 shipping) beats a 500 combinable discount…
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotions = new[]
        {
            Promo(P1, minimumAmountMinor: 1_000, grantsFreeShipping: true),
            Promo(P2, minimumAmountMinor: 1_000, discountAmountMinor: 500, combinable: true),
        };

        var withShipping = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", 2_000, Now);
        Assert.True(withShipping.FreeShippingApplied);
        Assert.Equal([P1], withShipping.AppliedPromotionIds);

        // …and loses when the cart already ships free (shippingMinor 0 → the free-shipping benefit is 0),
        // which is why checkout must evaluate AFTER the shipping waivers.
        var withoutShipping = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", 0, Now);
        Assert.Equal(500, withoutShipping.DiscountMinor);
        Assert.Equal([P2], withoutShipping.AppliedPromotionIds);
    }

    [Fact]
    public void A_currency_mismatch_is_ineligible()
    {
        // No FX (ADR-0041): a EUR promotion never applies to an AUD cart.
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotion = Promo(P1, minimumAmountMinor: 1_000, percentOff: 50, currency: "EUR");

        var outcome = PromotionEvaluator.Evaluate(lines, [promotion], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(0, outcome.DiscountMinor);
    }

    [Fact]
    public void A_window_that_has_not_started_or_has_expired_is_ineligible()
    {
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var notStarted = Promo(P1, minimumAmountMinor: 1_000, percentOff: 10);
        notStarted.ActiveFrom = Now.AddDays(1);
        var expired = Promo(P2, minimumAmountMinor: 1_000, percentOff: 10);
        expired.ActiveUntil = Now.AddDays(-1);

        Assert.Equal(0, PromotionEvaluator.Evaluate(lines, [notStarted], Tenant, Storefront, "AUD", 499, Now).DiscountMinor);
        Assert.Equal(0, PromotionEvaluator.Evaluate(lines, [expired], Tenant, Storefront, "AUD", 499, Now).DiscountMinor);
    }

    [Fact]
    public void Window_bounds_are_inclusive_and_open_ended_nulls_apply()
    {
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var starting = Promo(P1, minimumAmountMinor: 1_000, percentOff: 10);
        starting.ActiveFrom = Now;
        var ending = Promo(P2, minimumAmountMinor: 1_000, percentOff: 10);
        ending.ActiveUntil = Now;
        var openEnded = Promo(P3, minimumAmountMinor: 1_000, percentOff: 10);

        Assert.Equal(1_000, PromotionEvaluator.Evaluate(lines, [starting], Tenant, Storefront, "AUD", 499, Now).DiscountMinor);
        Assert.Equal(1_000, PromotionEvaluator.Evaluate(lines, [ending], Tenant, Storefront, "AUD", 499, Now).DiscountMinor);
        Assert.Equal(1_000, PromotionEvaluator.Evaluate(lines, [openEnded], Tenant, Storefront, "AUD", 499, Now).DiscountMinor);
    }

    [Fact]
    public void A_storefront_mismatch_is_ineligible_and_an_all_storefront_promotion_applies()
    {
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var other = Promo(P1, minimumAmountMinor: 1_000, percentOff: 10);
        other.StorefrontId = new Guid("33333333-3333-3333-3333-333333333333");
        var allStorefronts = Promo(P2, minimumAmountMinor: 1_000, percentOff: 10);

        Assert.Equal(0, PromotionEvaluator.Evaluate(lines, [other], Tenant, Storefront, "AUD", 499, Now).DiscountMinor);
        Assert.Equal(1_000, PromotionEvaluator.Evaluate(lines, [allStorefronts], Tenant, Storefront, "AUD", 499, Now).DiscountMinor);
    }

    [Fact]
    public void An_inactive_or_other_tenants_promotion_is_ineligible()
    {
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var inactive = Promo(P1, minimumAmountMinor: 1_000, percentOff: 10);
        inactive.Active = false;
        var otherTenant = Promo(P2, minimumAmountMinor: 1_000, percentOff: 10);
        otherTenant.TenantId = new Guid("44444444-4444-4444-4444-444444444444");

        Assert.Equal(0, PromotionEvaluator.Evaluate(lines, [inactive], Tenant, Storefront, "AUD", 499, Now).DiscountMinor);
        Assert.Equal(0, PromotionEvaluator.Evaluate(lines, [otherTenant], Tenant, Storefront, "AUD", 499, Now).DiscountMinor);
    }

    [Fact]
    public void The_line_allocation_sums_exactly_to_the_discount_on_an_odd_subtotal()
    {
        // 3 lines of 333 = 999; 10% = 99. A naive per-line 333×10/100 = 33 each also sums to 99, so use a
        // subtotal that genuinely forces rounding: 3 × 334 = 1002, 10% = 100, per line 33.4 → 33/33/34.
        var lines = new[] { Line(ProductA, 334, 1), Line(ProductB, 334, 1), Line(ProductA, 334, 1) };
        var promotion = Promo(P1, minimumAmountMinor: 1_000, percentOff: 10);

        var outcome = PromotionEvaluator.Evaluate(lines, promotions: [promotion], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(100, outcome.DiscountMinor);
        Assert.Equal(100, outcome.LineDiscountsMinor.Sum());
        Assert.Equal(3, outcome.LineDiscountsMinor.Count);
    }

    [Fact]
    public void The_allocation_sums_exactly_when_uneven_line_values_force_remainders()
    {
        // 1000 + 333 + 1 = 1334; 25% = 333. Every largest-remainder leftover must land somewhere.
        var lines = new[] { Line(ProductA, 1_000, 1), Line(ProductB, 333, 1), Line(ProductA, 1, 1) };
        var promotion = Promo(P1, minimumAmountMinor: 1_000, percentOff: 25);

        var outcome = PromotionEvaluator.Evaluate(lines, [promotion], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(333, outcome.DiscountMinor);
        Assert.Equal(333, outcome.LineDiscountsMinor.Sum());
        Assert.All(outcome.LineDiscountsMinor, d => Assert.True(d >= 0));
    }

    [Fact]
    public void The_combined_discount_is_capped_at_the_subtotal_and_the_allocation_still_sums()
    {
        // Three combinable 50%-off promotions would total 150% of the cart; the stack is capped at the
        // subtotal so the goods land at exactly zero, never negative.
        var lines = new[] { Line(ProductA, 600, 1), Line(ProductB, 401, 1) };
        var promotions = new[]
        {
            Promo(P1, minimumAmountMinor: 100, percentOff: 50, combinable: true),
            Promo(P2, minimumAmountMinor: 100, percentOff: 50, combinable: true),
            Promo(P3, minimumAmountMinor: 100, percentOff: 50, combinable: true),
        };

        var outcome = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(1_001, outcome.DiscountMinor);
        Assert.Equal(1_001, outcome.LineDiscountsMinor.Sum());
        Assert.Equal(600, outcome.LineDiscountsMinor[0]);
        Assert.Equal(401, outcome.LineDiscountsMinor[1]);
    }

    [Fact]
    public void Stacked_promotions_never_over_discount_a_single_line()
    {
        // A product-scoped 100%-off on the 100 line PLUS a cart-wide 10% (which would also want 10 of that
        // line) over-allocates line 0; the excess must move to the line with headroom, sum preserved.
        var lines = new[] { Line(ProductA, 100, 1), Line(ProductB, 900, 1) };
        var promotions = new[]
        {
            Promo(P1, scope: PromotionScopeKind.Product, productId: ProductA, minimumAmountMinor: 100, percentOff: 100, combinable: true),
            Promo(P2, minimumAmountMinor: 100, percentOff: 10, combinable: true),
        };

        var outcome = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(200, outcome.DiscountMinor);
        Assert.Equal(200, outcome.LineDiscountsMinor.Sum());
        Assert.Equal(100, outcome.LineDiscountsMinor[0]); // capped at the line's own value
        Assert.Equal(100, outcome.LineDiscountsMinor[1]);
    }

    [Fact]
    public void An_empty_cart_or_no_promotions_yields_the_none_outcome()
    {
        var lines = new[] { Line(ProductA, 1_000, 1) };

        var noPromotions = PromotionEvaluator.Evaluate(lines, [], Tenant, Storefront, "AUD", 499, Now);
        Assert.Equal(0, noPromotions.DiscountMinor);
        Assert.False(noPromotions.FreeShippingApplied);
        Assert.Single(noPromotions.LineDiscountsMinor);

        var noLines = PromotionEvaluator.Evaluate(
            [], [Promo(P1, minimumAmountMinor: 1, percentOff: 10)], Tenant, Storefront, "AUD", 499, Now);
        Assert.Equal(0, noLines.DiscountMinor);
        Assert.Empty(noLines.LineDiscountsMinor);
    }

    private static PromotionLine Line(Guid productId, long unitPriceMinor, int quantity) =>
        new(productId, unitPriceMinor, quantity);

    private static PromotionCopy Promo(
        Guid id,
        PromotionScopeKind scope = PromotionScopeKind.Storefront,
        Guid? productId = null,
        long minimumAmountMinor = 0,
        int minimumQuantity = 0,
        bool grantsFreeShipping = false,
        int percentOff = 0,
        long discountAmountMinor = 0,
        bool combinable = false,
        string currency = "AUD") =>
        new()
        {
            PromotionId = id,
            TenantId = Tenant,
            StorefrontId = null,
            Name = $"Promotion {id}",
            Currency = currency,
            Scope = scope,
            ProductId = productId,
            MinimumAmountMinor = minimumAmountMinor,
            MinimumQuantity = minimumQuantity,
            GrantsFreeShipping = grantsFreeShipping,
            PercentOff = percentOff,
            DiscountAmountMinor = discountAmountMinor,
            Combinable = combinable,
            Active = true,
        };
}
