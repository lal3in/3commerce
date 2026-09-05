using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// Coupon codes (ADR-0052): a coupon is a CODE-GATED promotion, so these facts pin the two things that
/// are genuinely new — the gate itself (an automatic promotion still applies without a code; a gated one
/// only ever applies with the right one) and the shopper-facing REASON a code was refused. Everything
/// else (thresholds, rewards, stacking, allocation) is the ADR-0051 evaluator, unchanged.
/// </summary>
public class CouponTests
{
    private static readonly Guid Tenant = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenant = new("11111111-1111-1111-1111-1111111111ff");
    private static readonly Guid Storefront = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherStorefront = new("22222222-2222-2222-2222-2222222222ff");
    private static readonly Guid ProductA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid P1 = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid P2 = new("00000000-0000-0000-0000-0000000000a2");

    // ---- The gate ------------------------------------------------------------------------------------

    [Fact]
    public void A_code_gated_promotion_does_nothing_without_the_code()
    {
        var lines = new[] { Line(10_000, 1) };
        var coupon = Promo(P1, code: "WELCOME10", percentOff: 10);

        var outcome = PromotionEvaluator.Evaluate(lines, [coupon], Tenant, Storefront, "AUD", 499, Now);

        Assert.Equal(0, outcome.DiscountMinor);
        Assert.Empty(outcome.AppliedPromotionIds);
    }

    [Fact]
    public void A_code_gated_promotion_applies_when_the_code_matches_case_insensitively()
    {
        var lines = new[] { Line(10_000, 1) };
        var coupon = Promo(P1, code: "WELCOME10", percentOff: 10);

        var outcome = PromotionEvaluator.Evaluate(lines, [coupon], Tenant, Storefront, "AUD", 499, Now, "  welcome10 ");

        Assert.Equal(1_000, outcome.DiscountMinor);
        Assert.Equal([P1], outcome.AppliedPromotionIds);
    }

    [Fact]
    public void The_wrong_code_never_unlocks_a_coupon()
    {
        var lines = new[] { Line(10_000, 1) };
        var coupon = Promo(P1, code: "WELCOME10", percentOff: 10);

        var outcome = PromotionEvaluator.Evaluate(lines, [coupon], Tenant, Storefront, "AUD", 499, Now, "WELCOME20");

        Assert.Equal(0, outcome.DiscountMinor);
    }

    [Fact]
    public void An_automatic_promotion_is_unaffected_by_an_entered_code()
    {
        // Regression guard for every pre-coupon promotion: entering a code must not switch them off.
        var lines = new[] { Line(10_000, 1) };
        var automatic = Promo(P1, minimumAmountMinor: 5_000, percentOff: 10);

        Assert.Equal(1_000, PromotionEvaluator.Evaluate(lines, [automatic], Tenant, Storefront, "AUD", 499, Now).DiscountMinor);
        Assert.Equal(1_000, PromotionEvaluator.Evaluate(lines, [automatic], Tenant, Storefront, "AUD", 499, Now, "ANYTHING").DiscountMinor);
    }

    [Fact]
    public void A_combinable_coupon_stacks_with_a_combinable_automatic_promotion()
    {
        // Stacking is NOT a new concept: the coupon is a promotion, so Combinable governs it (ADR-0052).
        var lines = new[] { Line(10_000, 1) };
        var coupon = Promo(P1, code: "SAVE10", percentOff: 10, combinable: true);
        var automatic = Promo(P2, minimumAmountMinor: 5_000, percentOff: 5, combinable: true);

        var outcome = PromotionEvaluator.Evaluate(lines, [coupon, automatic], Tenant, Storefront, "AUD", 499, Now, "SAVE10");

        Assert.Equal(1_500, outcome.DiscountMinor);
        Assert.Equal([P1, P2], outcome.AppliedPromotionIds);
    }

    [Fact]
    public void An_exclusive_coupon_wins_only_if_it_beats_the_best_automatic_promotion()
    {
        var lines = new[] { Line(10_000, 1) };
        var coupon = Promo(P1, code: "SAVE5", percentOff: 5);
        var automatic = Promo(P2, minimumAmountMinor: 5_000, percentOff: 20);

        var outcome = PromotionEvaluator.Evaluate(lines, [coupon, automatic], Tenant, Storefront, "AUD", 499, Now, "SAVE5");

        // The shopper keeps the better deal; the coupon simply loses (and so is never redeemed).
        Assert.Equal(2_000, outcome.DiscountMinor);
        Assert.Equal([P2], outcome.AppliedPromotionIds);
    }

    // ---- The reason ----------------------------------------------------------------------------------

    [Fact]
    public void No_code_entered_is_reported_as_None()
    {
        Assert.Equal(CouponStatus.None, Evaluate(null, Promo(P1, code: "X", percentOff: 10)).Status);
        Assert.Equal(CouponStatus.None, Evaluate("   ", Promo(P1, code: "X", percentOff: 10)).Status);
    }

    [Fact]
    public void An_unresolved_code_is_UnknownCode()
    {
        Assert.Equal(CouponStatus.UnknownCode, Evaluate("NOPE", null).Status);
    }

    [Fact]
    public void An_automatic_promotion_can_never_be_redeemed_as_a_coupon()
    {
        // Guard against a code matching a promotion that carries none (a NULL = NULL style accident).
        var automatic = Promo(P1, minimumAmountMinor: 1_000, percentOff: 10);

        Assert.Equal(CouponStatus.UnknownCode, Evaluate("ANY", automatic).Status);
    }

    [Fact]
    public void A_switched_off_coupon_is_Inactive()
    {
        var coupon = Promo(P1, code: "OFF", percentOff: 10);
        coupon.Active = false;

        Assert.Equal(CouponStatus.Inactive, Evaluate("OFF", coupon).Status);
    }

    [Fact]
    public void A_window_that_has_not_opened_is_NotStarted_and_one_that_closed_is_Expired()
    {
        var early = Promo(P1, code: "SOON", percentOff: 10);
        early.ActiveFrom = Now.AddDays(1);
        Assert.Equal(CouponStatus.NotStarted, Evaluate("SOON", early).Status);

        var late = Promo(P1, code: "GONE", percentOff: 10);
        late.ActiveUntil = Now.AddDays(-1);
        Assert.Equal(CouponStatus.Expired, Evaluate("GONE", late).Status);
    }

    [Fact]
    public void Another_storefront_another_tenant_or_another_currency_is_WrongStorefront()
    {
        var scoped = Promo(P1, code: "MINE", percentOff: 10);
        scoped.StorefrontId = OtherStorefront;
        Assert.Equal(CouponStatus.WrongStorefront, Evaluate("MINE", scoped).Status);

        var foreignTenant = Promo(P1, code: "MINE", percentOff: 10);
        foreignTenant.TenantId = OtherTenant;
        Assert.Equal(CouponStatus.WrongStorefront, Evaluate("MINE", foreignTenant).Status);

        // No-FX (ADR-0041): a EUR coupon can never apply to an AUD cart, and the shopper is told so.
        var foreignCurrency = Promo(P1, code: "MINE", percentOff: 10, currency: "EUR");
        Assert.Equal(CouponStatus.WrongStorefront, Evaluate("MINE", foreignCurrency).Status);
    }

    [Fact]
    public void An_exhausted_coupon_is_UsageLimitReached_even_when_the_cart_would_not_qualify()
    {
        // Ordering matters: telling a shopper to spend more on a code that is already used up is a lie.
        var coupon = Promo(P1, code: "LAST", percentOff: 10, minimumAmountMinor: 99_000);
        coupon.MaxRedemptions = 5;

        Assert.Equal(CouponStatus.UsageLimitReached, Evaluate("LAST", coupon, heldRedemptions: 5).Status);
        Assert.Equal(CouponStatus.ThresholdNotMet, Evaluate("LAST", coupon, heldRedemptions: 4).Status);
    }

    [Fact]
    public void A_repeat_customer_is_CustomerLimitReached()
    {
        var coupon = Promo(P1, code: "ONCE", percentOff: 10);
        coupon.MaxRedemptionsPerCustomer = 1;

        Assert.Equal(CouponStatus.CustomerLimitReached, Evaluate("ONCE", coupon, customerHeld: 1).Status);
        Assert.Equal(CouponStatus.Applied, Evaluate("ONCE", coupon, customerHeld: 0).Status);
    }

    [Fact]
    public void A_cart_below_the_coupons_threshold_is_ThresholdNotMet()
    {
        var coupon = Promo(P1, code: "SPEND100", percentOff: 10, minimumAmountMinor: 100_000);

        Assert.Equal(CouponStatus.ThresholdNotMet, Evaluate("SPEND100", coupon).Status);
    }

    [Fact]
    public void A_valid_coupon_is_Applied_and_carries_its_promotion_identity()
    {
        var coupon = Promo(P1, code: "WELCOME10", percentOff: 10);

        var evaluation = Evaluate("welcome10", coupon);

        Assert.Equal(CouponStatus.Applied, evaluation.Status);
        Assert.True(evaluation.IsApplied);
        Assert.Equal(P1, evaluation.PromotionId);
        Assert.Equal(coupon.Name, evaluation.Name);
    }

    // ---- Keys and normalization ----------------------------------------------------------------------

    [Fact]
    public void Normalize_matches_Catalogs_canonical_form()
    {
        Assert.Null(CouponValidator.Normalize(null));
        Assert.Null(CouponValidator.Normalize("  "));
        Assert.Equal("WELCOME10", CouponValidator.Normalize("  welcome10 "));
    }

    [Fact]
    public void CustomerKey_prefers_the_signed_in_user_and_falls_back_to_the_normalized_email()
    {
        var user = new Guid("33333333-3333-3333-3333-333333333333");

        Assert.Equal($"u:{user}", PromotionRedemption.CustomerKeyFor(user, "shopper@example.com"));
        Assert.Equal("e:shopper@example.com", PromotionRedemption.CustomerKeyFor(null, "  Shopper@Example.com "));
        Assert.Equal("e:shopper@example.com", PromotionRedemption.CustomerKeyFor(Guid.Empty, "Shopper@Example.com"));
        Assert.Null(PromotionRedemption.CustomerKeyFor(null, "   "));
    }

    [Fact]
    public void A_redemption_counts_against_the_limits_until_it_is_released()
    {
        var redemption = new PromotionRedemption { CustomerKey = "e:a@b.c", Code = "X" };

        Assert.True(redemption.IsHeld);
        redemption.Status = PromotionRedemptionStatus.Confirmed;
        Assert.True(redemption.IsHeld);
        redemption.Status = PromotionRedemptionStatus.Released;
        Assert.False(redemption.IsHeld);
    }

    private static CouponEvaluation Evaluate(
        string? enteredCode, PromotionCopy? promotion, int heldRedemptions = 0, int customerHeld = 0) =>
        CouponValidator.Evaluate(
            enteredCode, promotion, [Line(10_000, 1)], Tenant, Storefront, "AUD", Now, heldRedemptions, customerHeld);

    private static PromotionLine Line(long unitPriceMinor, int quantity) =>
        new(ProductA, unitPriceMinor, quantity);

    private static PromotionCopy Promo(
        Guid id,
        string? code = null,
        long minimumAmountMinor = 0,
        int percentOff = 0,
        bool combinable = false,
        string currency = "AUD") =>
        new()
        {
            PromotionId = id,
            TenantId = Tenant,
            StorefrontId = null,
            Name = $"Promotion {id}",
            Currency = currency,
            Scope = PromotionScopeKind.Storefront,
            MinimumAmountMinor = minimumAmountMinor,
            PercentOff = percentOff,
            Combinable = combinable,
            Active = true,
            Code = code,
        };
}
