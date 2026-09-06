using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// The PREVIEW half of the shared promotion decision (ADR-0051): what <c>GET /cart/summary</c> may tell a
/// shopper when the carrier rate is not known yet. Checkout is authoritative and unchanged — every test
/// here compares the preview against <see cref="PromotionEvaluator.Evaluate"/>, which is exactly what
/// checkout runs, at the rate checkout would use.
/// <para>
/// The contract under test: the preview NEVER contradicts the charge. It is either provably identical to
/// it for every possible shipping amount (<see cref="PromotionBasis.Settled"/>), identical to it because
/// the shipping amount is known (<see cref="PromotionBasis.Quoted"/>), or explicitly
/// <see cref="PromotionBasis.Provisional"/> — reporting the guaranteed floor and leaving free shipping
/// undecided.
/// </para>
/// </summary>
public class PromotionPreviewTests
{
    private static readonly Guid Tenant = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Storefront = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ProductA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid P1 = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid P2 = new("00000000-0000-0000-0000-0000000000a2");

    /// <summary>The rate the preview used to guess before this fix (CheckoutEndpoints.FlatShippingMinor).</summary>
    private const long FlatFallback = 499;

    [Fact]
    public void The_flat_fallback_guess_picked_a_different_winner_than_the_real_rate()
    {
        // THE REGRESSION. Cart 10000. P1 grants free shipping (exclusive); P2 is a 900 cash discount.
        //   benefit(P1) = 0 + S      benefit(P2) = 900
        // At the old preview basis (the flat 499 fallback) P2 wins: 900 > 499.
        // At a real carrier rate of 1500 — a perfectly ordinary interstate/express rate — P1 wins.
        // Same cart, same promotions, two different winners: the shopper was shown one reward and
        // charged under another.
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotions = FreeShippingVersusCashDiscount();

        var atFallback = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", FlatFallback, Now);
        var atRealRate = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", 1_500, Now);

        Assert.Equal([P2], atFallback.AppliedPromotionIds);
        Assert.False(atFallback.FreeShippingApplied);
        Assert.Equal([P1], atRealRate.AppliedPromotionIds);
        Assert.True(atRealRate.FreeShippingApplied);

        // The preview no longer guesses: with no rate to score against it refuses to commit to either.
        var guessed = PromotionEvaluator.Preview(lines, promotions, Tenant, Storefront, "AUD", null, Now);
        Assert.Equal(PromotionBasis.Provisional, guessed.Basis);
    }

    [Fact]
    public void A_known_rate_makes_the_preview_identical_to_the_charge()
    {
        // The shipping address is known (a signed-in shopper's default address, or a guest's entered one),
        // so the storefront quotes the SAME carrier endpoint checkout's selected rate comes from and hands
        // the amount to the preview. Preview and charge are then the same computation on the same input.
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotions = FreeShippingVersusCashDiscount();

        foreach (var rate in new long[] { 0, 1, 499, 899, 900, 901, 1_500, 9_999, 100_000 })
        {
            var preview = PromotionEvaluator.Preview(lines, promotions, Tenant, Storefront, "AUD", rate, Now);
            var charge = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", rate, Now);

            Assert.Equal(charge.DiscountMinor, preview.Outcome.DiscountMinor);
            Assert.Equal(charge.FreeShippingApplied, preview.Outcome.FreeShippingApplied);
            Assert.Equal(charge.AppliedPromotionIds, preview.Outcome.AppliedPromotionIds);
            Assert.Equal(charge.LineDiscountsMinor, preview.Outcome.LineDiscountsMinor);
        }
    }

    [Fact]
    public void A_cart_that_pays_no_shipping_at_all_is_unambiguous()
    {
        // No line requires shipping (all digital/service), so shipping is never charged and a free-shipping
        // reward is worth exactly 0 — the cash discount wins, deterministically. The preview passes 0 for
        // such a cart, so this whole class of divergence disappears rather than being flagged provisional.
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotions = FreeShippingVersusCashDiscount();

        var preview = PromotionEvaluator.Preview(lines, promotions, Tenant, Storefront, "AUD", 0, Now);

        Assert.Equal([P2], preview.Outcome.AppliedPromotionIds);
        Assert.False(preview.Outcome.FreeShippingApplied);
        Assert.Equal(900, preview.Outcome.DiscountMinor);
    }

    [Fact]
    public void A_decision_no_shipping_amount_can_change_is_settled()
    {
        // No free-shipping promotion in play: benefit is pure cash on both branches, so the winner is the
        // same at every rate. The preview says so — no rate needed, no provisional wording.
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotions = new[]
        {
            Promo(P1, minimumAmountMinor: 1_000, discountAmountMinor: 900),
            Promo(P2, minimumAmountMinor: 1_000, percentOff: 5),
        };

        var preview = PromotionEvaluator.Preview(lines, promotions, Tenant, Storefront, "AUD", null, Now);

        Assert.Equal(PromotionBasis.Settled, preview.Basis);
        Assert.Equal([P1], preview.Outcome.AppliedPromotionIds);
        foreach (var rate in new long[] { 0, 499, 5_000, 1_000_000 })
        {
            Assert.Equal(
                PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", rate, Now).AppliedPromotionIds,
                preview.Outcome.AppliedPromotionIds);
        }
    }

    [Fact]
    public void Free_shipping_that_wins_at_every_rate_is_settled_too()
    {
        // P1 grants free shipping AND out-discounts P2, so it wins even when shipping is worth nothing.
        // Free shipping is in genuine contention only when the rate could decide it — here it cannot, so
        // the preview commits (and shows the free-shipping row) with no hedging.
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotions = new[]
        {
            Promo(P1, minimumAmountMinor: 1_000, discountAmountMinor: 900, grantsFreeShipping: true),
            Promo(P2, minimumAmountMinor: 1_000, discountAmountMinor: 500),
        };

        var preview = PromotionEvaluator.Preview(lines, promotions, Tenant, Storefront, "AUD", null, Now);

        Assert.Equal(PromotionBasis.Settled, preview.Basis);
        Assert.True(preview.Outcome.FreeShippingApplied);
        Assert.Equal([P1], preview.Outcome.AppliedPromotionIds);
    }

    [Fact]
    public void A_provisional_preview_reports_the_guaranteed_floor()
    {
        // Shippable cart, no address yet: the winner genuinely depends on a rate nobody knows. The preview
        // reports the SMALLER of the two possible goods discounts, so whatever checkout charges the shopper
        // is never shown a saving they do not get. The free-shipping half is reported as possible, never
        // decided (the endpoint maps Provisional onto "may apply at checkout").
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var promotions = FreeShippingVersusCashDiscount();

        var preview = PromotionEvaluator.Preview(lines, promotions, Tenant, Storefront, "AUD", null, Now);

        Assert.Equal(PromotionBasis.Provisional, preview.Basis);
        Assert.Equal(0, preview.Outcome.DiscountMinor); // P1 is a pure free-shipping reward: 0 off the goods
        for (long rate = 0; rate <= 3_000; rate += 37)
        {
            var charge = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", rate, Now);
            Assert.True(
                preview.Outcome.DiscountMinor <= charge.DiscountMinor,
                $"preview {preview.Outcome.DiscountMinor} must never exceed the charge {charge.DiscountMinor} at rate {rate}");
        }
    }

    [Fact]
    public void A_provisional_preview_still_allocates_exactly()
    {
        // The money invariant does not bend for a provisional preview: the per-line vector must still sum
        // to the reported discount EXACTLY, or the caller's tax base (and Net + Ship + Tax = Gross) breaks.
        var lines = new[] { Line(ProductA, 334, 1), Line(ProductA, 333, 1), Line(ProductA, 333, 1) };
        var promotions = new[]
        {
            Promo(P1, minimumAmountMinor: 100, grantsFreeShipping: true, percentOff: 10, combinable: true),
            Promo(P2, minimumAmountMinor: 100, discountAmountMinor: 300),
        };

        var preview = PromotionEvaluator.Preview(lines, promotions, Tenant, Storefront, "AUD", null, Now);

        Assert.Equal(PromotionBasis.Provisional, preview.Basis);
        Assert.Equal(preview.Outcome.DiscountMinor, preview.Outcome.LineDiscountsMinor.Sum());
        Assert.Equal(lines.Length, preview.Outcome.LineDiscountsMinor.Count);
    }

    [Fact]
    public void An_empty_cart_or_no_promotions_is_settled_and_empty()
    {
        var settledEmpty = PromotionEvaluator.Preview([], [Promo(P1, minimumAmountMinor: 1, percentOff: 10)], Tenant, Storefront, "AUD", null, Now);
        Assert.Equal(PromotionBasis.Settled, settledEmpty.Basis);
        Assert.Empty(settledEmpty.Outcome.LineDiscountsMinor);

        var noPromotions = PromotionEvaluator.Preview([Line(ProductA, 1_000, 1)], [], Tenant, Storefront, "AUD", null, Now);
        Assert.Equal(PromotionBasis.Settled, noPromotions.Basis);
        Assert.Equal(0, noPromotions.Outcome.DiscountMinor);
    }

    [Fact]
    public void A_coupon_is_previewed_on_the_same_basis_as_the_charge()
    {
        // A code-gated cash discount racing an automatic free-shipping promotion is the same contest, so
        // the same rules apply: unknown rate → provisional; known rate → identical to the charge.
        var lines = new[] { Line(ProductA, 10_000, 1) };
        var coupon = Promo(P2, minimumAmountMinor: 0, discountAmountMinor: 900);
        coupon.Code = "SAVE9";
        var promotions = new[] { Promo(P1, minimumAmountMinor: 1_000, grantsFreeShipping: true), coupon };

        Assert.Equal(
            PromotionBasis.Provisional,
            PromotionEvaluator.Preview(lines, promotions, Tenant, Storefront, "AUD", null, Now, "SAVE9").Basis);

        var quoted = PromotionEvaluator.Preview(lines, promotions, Tenant, Storefront, "AUD", 1_500, Now, "SAVE9");
        var charged = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", 1_500, Now, "SAVE9");
        Assert.Equal(PromotionBasis.Quoted, quoted.Basis);
        Assert.Equal(charged.AppliedPromotionIds, quoted.Outcome.AppliedPromotionIds);
    }

    [Fact]
    public void The_preview_never_contradicts_the_charge_for_any_promotion_mix()
    {
        // The property the whole design rests on, swept over pseudo-random promotion mixes and rates:
        //   Settled     ⇒ the preview IS the charge, at every rate (the two-probe proof holds);
        //   Provisional ⇒ the preview's goods discount is a floor the charge always meets or beats,
        //                 and free shipping was in genuine contention (never a silent commitment);
        //   Quoted      ⇒ the preview IS the charge at the quoted rate.
        var random = new Random(20260906);
        for (var trial = 0; trial < 400; trial++)
        {
            var lines = new[]
            {
                Line(ProductA, random.Next(1, 40) * 100L, random.Next(1, 4)),
                Line(ProductA, random.Next(1, 40) * 100L, random.Next(1, 4)),
            };
            var promotions = Enumerable.Range(0, random.Next(1, 5))
                .Select(i => Promo(
                    new Guid($"00000000-0000-0000-0000-0000000000{i:x2}"),
                    minimumAmountMinor: random.Next(0, 3) * 500L,
                    grantsFreeShipping: random.Next(0, 2) == 1,
                    percentOff: random.Next(0, 2) == 1 ? random.Next(0, 30) : 0,
                    discountAmountMinor: random.Next(0, 2) == 1 ? random.Next(0, 2_000) : 0,
                    combinable: random.Next(0, 2) == 1))
                .ToArray();

            var preview = PromotionEvaluator.Preview(lines, promotions, Tenant, Storefront, "AUD", null, Now);
            for (long rate = 0; rate <= 6_000; rate += 13)
            {
                var charge = PromotionEvaluator.Evaluate(lines, promotions, Tenant, Storefront, "AUD", rate, Now);
                if (preview.Basis == PromotionBasis.Settled)
                {
                    Assert.Equal(charge.DiscountMinor, preview.Outcome.DiscountMinor);
                    Assert.Equal(charge.FreeShippingApplied, preview.Outcome.FreeShippingApplied);
                    Assert.Equal(charge.AppliedPromotionIds, preview.Outcome.AppliedPromotionIds);
                    Assert.Equal(charge.LineDiscountsMinor, preview.Outcome.LineDiscountsMinor);
                }
                else
                {
                    Assert.True(preview.Outcome.DiscountMinor <= charge.DiscountMinor);
                }

                var quoted = PromotionEvaluator.Preview(lines, promotions, Tenant, Storefront, "AUD", rate, Now);
                Assert.Equal(charge.DiscountMinor, quoted.Outcome.DiscountMinor);
                Assert.Equal(charge.FreeShippingApplied, quoted.Outcome.FreeShippingApplied);
                Assert.Equal(charge.AppliedPromotionIds, quoted.Outcome.AppliedPromotionIds);
                Assert.Equal(charge.LineDiscountsMinor, quoted.Outcome.LineDiscountsMinor);
            }

            // A provisional verdict is only ever justified by free shipping being in genuine contention.
            if (preview.Basis == PromotionBasis.Provisional)
            {
                Assert.True(preview.Outcome.FreeShippingApplied);
            }
        }
    }

    /// <summary>
    /// The head-to-head that used to diverge: a pure free-shipping reward (worth the shipping amount,
    /// whatever it turns out to be) against a 900-minor cash discount (worth 900 always).
    /// </summary>
    private static PromotionCopy[] FreeShippingVersusCashDiscount() =>
    [
        Promo(P1, minimumAmountMinor: 1_000, grantsFreeShipping: true),
        Promo(P2, minimumAmountMinor: 1_000, discountAmountMinor: 900),
    ];

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
