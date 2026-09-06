using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// The discount × promotion × shipping combination space, asserted where the money actually is: the cart
/// PREVIEW (<c>GET /cart/summary</c>) and the CHARGE (<c>POST /checkout</c>), on the same cart, in the
/// same test — so "shown == charged" is proved rather than assumed (ADR-0051/0052/0053).
/// <para>
/// The headline case is the preview/charge divergence this suite was written for: a free-shipping
/// promotion racing a cash discount, where the real carrier rate differs materially from the flat
/// fallback the preview used to guess. The rest of the matrix pins the surrounding behaviour so a
/// selection change cannot quietly move a neighbouring case.
/// </para>
/// <para>
/// Every money case asserts the identity <c>Net − Discount + Ship + Tax = Gross</c> (Net is the
/// PRE-discount subtotal), the subtotal cap, and — on the paths that settle — a trial balance of 0.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class PromotionMatrixTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>The rate the preview used to guess before the parity fix (CheckoutEndpoints.FlatShippingMinor).</summary>
    private const long FlatFallback = 499;

    // CheckoutResponse's promotion fields are APPENDED positional record members with defaults (ADR-0051).
    private sealed record CheckoutResponseDto(
        Guid OrderId, string ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
        long GrossMinor, string Currency, string? Message, bool FreeShippingApplied = false,
        List<Guid>? AppliedPromotionIds = null, string? CouponCode = null);

    private sealed record AppliedPromotionDto(Guid PromotionId, string Name, long DiscountMinor);

    private sealed record SummaryDto(
        long SubtotalMinor, long StorefrontDiscountMinor, long PromotionDiscountMinor, long ItemsTotalMinor,
        bool FreeShippingApplied, List<AppliedPromotionDto> AppliedPromotions, string Currency,
        int CouponStatus = 0, string? CouponCode = null, string CouponPromotionName = "", int Basis = 0);

    /// <summary>Mirrors Ordering's PromotionBasis; enums cross HTTP as numbers (platform invariant).</summary>
    private const int Settled = 0;
    private const int Quoted = 1;
    private const int Provisional = 2;

    // ---------------------------------------------------------------------------------------------
    // The regression: preview vs charge when the real rate is not the flat fallback
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Free_shipping_racing_a_cash_discount_previews_exactly_what_checkout_charges()
    {
        // THE DIVERGENCE. Cart 10000, shippable. Two exclusive promotions:
        //   FREE SHIP — worth the shipping amount, whatever it turns out to be
        //   SAVE 9.00 — worth 900, always
        // Scored at the old flat fallback (499) the cash discount wins; scored at the real carrier rate
        // (1500) free shipping wins. The preview used to guess 499 and could therefore show one reward
        // while checkout charged under the other.
        //
        // A shopper who has entered a shipping address (guest or signed in) now has that address quoted,
        // and the SAME rate rides both the preview and the checkout POST — so the two agree exactly.
        const string currency = "QJJ";
        var storefrontId = await SeedStorefrontAsync(currency, "Rate Race Store");
        var freeShipId = await SeedPromotionAsync(storefrontId, currency, "Free shipping over 50", grantsFreeShipping: true, minimumAmountMinor: 5_000);
        var cashId = await SeedPromotionAsync(storefrontId, currency, "9.00 off over 50", discountAmountMinor: 900, minimumAmountMinor: 5_000);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = Shopper(storefrontId);
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        // The preview, scored against the quoted rate for the shopper's destination.
        var preview = await SummaryAsync(shopper, storefrontId, shippingMinor: 1_500, shipToCountry: "DE");
        Assert.Equal(Quoted, preview.Basis);
        Assert.True(preview.FreeShippingApplied);
        Assert.Equal(0, preview.PromotionDiscountMinor); // a pure free-shipping reward takes nothing off the goods
        Assert.Equal(freeShipId, Assert.Single(preview.AppliedPromotions).PromotionId);

        // And the guess that used to be made: at 499 the OTHER promotion wins. Same cart, same promotions
        // — which is exactly why the preview must never invent a rate. (Probed before checkout, which
        // consumes the cart.)
        var atFallback = await SummaryAsync(shopper, storefrontId, shippingMinor: FlatFallback, shipToCountry: "DE");
        Assert.False(atFallback.FreeShippingApplied);
        Assert.Equal(900, atFallback.PromotionDiscountMinor);
        Assert.Equal(cashId, Assert.Single(atFallback.AppliedPromotions).PromotionId);

        // The charge, on the same rate the shopper was quoted.
        var order = await CheckoutAsync(shopper, shippingMinor: 1_500);
        Assert.True(order.FreeShippingApplied);
        Assert.Equal(freeShipId, Assert.Single(order.AppliedPromotionIds!));
        Assert.Equal(preview.PromotionDiscountMinor, order.DiscountMinor);
        Assert.Equal(0, order.ShippingMinor); // free shipping zeroes the quoted 1500
        AssertMoneyIdentity(order);
        Assert.Equal(10_000, order.GrossMinor);

        await SettleAsync(shopper, order);
    }

    [Fact]
    public async Task With_no_address_yet_the_preview_is_provisional_instead_of_guessing()
    {
        // The ONLY case the preview cannot decide: a cart with shippable lines and no address anywhere —
        // no saved default, nothing entered. It refuses to commit: free shipping is reported as possible
        // (Provisional), and the goods discount shown is the guaranteed FLOOR, so whatever rate the
        // shopper's address later produces, they are never shown a saving they do not get.
        const string currency = "QKK";
        var storefrontId = await SeedStorefrontAsync(currency, "No Address Store");
        await SeedPromotionAsync(storefrontId, currency, "Free shipping over 50", grantsFreeShipping: true, minimumAmountMinor: 5_000);
        await SeedPromotionAsync(storefrontId, currency, "9.00 off over 50", discountAmountMinor: 900, minimumAmountMinor: 5_000);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = Shopper(storefrontId);
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var preview = await SummaryAsync(shopper, storefrontId);
        Assert.Equal(Provisional, preview.Basis);
        // Never asserted as decided — the storefront renders "free shipping may apply" from the basis.
        Assert.False(preview.FreeShippingApplied);
        Assert.Equal(0, preview.PromotionDiscountMinor);

        // Whatever rate the shopper's address later produces, the charge meets or beats the floor.
        foreach (var rate in new long[] { 0, FlatFallback, 1_500 })
        {
            var quoted = await SummaryAsync(shopper, storefrontId, shippingMinor: rate, shipToCountry: "DE");
            Assert.True(quoted.PromotionDiscountMinor >= preview.PromotionDiscountMinor);
        }

        var order = await CheckoutAsync(shopper, shippingMinor: 1_500);
        Assert.True(order.DiscountMinor >= preview.PromotionDiscountMinor);
        AssertMoneyIdentity(order);
    }

    [Fact]
    public async Task An_all_digital_cart_scores_free_shipping_at_zero_and_the_cash_discount_wins()
    {
        // A cart with no shipping-requiring line pays no shipping at all, so a free-shipping reward is
        // worth exactly 0 and the contest is unambiguous — no address, no quote, no provisional wording.
        // The preview knows this from the same OfferCopy/ProductType gate checkout uses.
        const string currency = "QLL";
        var storefrontId = await SeedStorefrontAsync(currency, "Digital Only Store");
        await SeedPromotionAsync(storefrontId, currency, "Free shipping over 10", grantsFreeShipping: true, minimumAmountMinor: 1_000);
        var cashId = await SeedPromotionAsync(storefrontId, currency, "9.00 off over 10", discountAmountMinor: 900, minimumAmountMinor: 1_000);
        var (productId, _) = await fixture.SeedSuppliedProductAsync(
            priceMinor: 10_000, supplierCostMinor: 0, currency: currency, fulfilmentType: FulfilmentType.DigitalDownload);

        using var shopper = Shopper(storefrontId);
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var preview = await SummaryAsync(shopper, storefrontId);
        Assert.NotEqual(Provisional, preview.Basis);
        Assert.False(preview.FreeShippingApplied);
        Assert.Equal(900, preview.PromotionDiscountMinor);
        Assert.Equal(cashId, Assert.Single(preview.AppliedPromotions).PromotionId);

        var order = await CheckoutAsync(shopper);
        Assert.Equal(0, order.ShippingMinor);
        Assert.Equal(900, order.DiscountMinor);
        Assert.Equal(cashId, Assert.Single(order.AppliedPromotionIds!));
        Assert.Equal(preview.PromotionDiscountMinor, order.DiscountMinor);
        AssertMoneyIdentity(order);
        await SettleAsync(shopper, order);
    }

    // ---------------------------------------------------------------------------------------------
    // Selection / stacking
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_exclusives_never_sum_only_the_better_one_applies()
    {
        // Combinable OFF on both: an exclusive never combines with anything, including another exclusive.
        // 1500 + 800 must NOT become 2300 — the better single reward wins alone.
        const string currency = "QMM";
        var storefrontId = await SeedStorefrontAsync(currency, "Exclusive Only Store");
        var betterId = await SeedPromotionAsync(storefrontId, currency, "15.00 off", discountAmountMinor: 1_500, minimumAmountMinor: 1_000);
        await SeedPromotionAsync(storefrontId, currency, "8.00 off", discountAmountMinor: 800, minimumAmountMinor: 1_000);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = Shopper(storefrontId);
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var preview = await SummaryAsync(shopper, storefrontId);
        Assert.Equal(Settled, preview.Basis); // no free shipping in play → shipping cannot change the winner
        Assert.Equal(1_500, preview.PromotionDiscountMinor);
        Assert.Equal(betterId, Assert.Single(preview.AppliedPromotions).PromotionId);

        var order = await CheckoutAsync(shopper);
        Assert.Equal(1_500, order.DiscountMinor);
        Assert.Equal(betterId, Assert.Single(order.AppliedPromotionIds!));
        AssertMoneyIdentity(order);
    }

    [Fact]
    public async Task A_single_exclusive_beats_a_smaller_stack_of_combinables()
    {
        // The mirror of MoneyFlowTests' combinable-wins case: here the exclusive 25% (2500) out-scores the
        // two stacked 10%s (2000), so the exclusive applies ALONE and neither combinable is reported.
        const string currency = "QNN";
        var storefrontId = await SeedStorefrontAsync(currency, "Exclusive Wins Store");
        var exclusiveId = await SeedPromotionAsync(storefrontId, currency, "25% off", percentOff: 25, minimumAmountMinor: 5_000);
        var stackA = await SeedPromotionAsync(storefrontId, currency, "Stack 10% A", percentOff: 10, minimumAmountMinor: 5_000, combinable: true);
        var stackB = await SeedPromotionAsync(storefrontId, currency, "Stack 10% B", percentOff: 10, minimumAmountMinor: 5_000, combinable: true);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = Shopper(storefrontId);
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var preview = await SummaryAsync(shopper, storefrontId);
        Assert.Equal(2_500, preview.PromotionDiscountMinor);
        Assert.Equal(exclusiveId, Assert.Single(preview.AppliedPromotions).PromotionId);

        var order = await CheckoutAsync(shopper);
        Assert.Equal(2_500, order.DiscountMinor);
        Assert.Equal([exclusiveId], order.AppliedPromotionIds);
        Assert.DoesNotContain(stackA, order.AppliedPromotionIds!);
        Assert.DoesNotContain(stackB, order.AppliedPromotionIds!);
        AssertMoneyIdentity(order);
        await SettleAsync(shopper, order);
    }

    [Fact]
    public async Task A_tie_goes_to_the_combinable_set()
    {
        // Equal benefit (2000 either way) → the combinable set wins, because it shows the shopper more
        // applied promotions for the same money (ADR-0051 decision 4).
        const string currency = "QPP";
        var storefrontId = await SeedStorefrontAsync(currency, "Tie Store");
        var exclusiveId = await SeedPromotionAsync(storefrontId, currency, "20% exclusive", percentOff: 20, minimumAmountMinor: 5_000);
        await SeedPromotionAsync(storefrontId, currency, "Stack 10% A", percentOff: 10, minimumAmountMinor: 5_000, combinable: true);
        await SeedPromotionAsync(storefrontId, currency, "Stack 10% B", percentOff: 10, minimumAmountMinor: 5_000, combinable: true);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = Shopper(storefrontId);
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var preview = await SummaryAsync(shopper, storefrontId);
        Assert.Equal(2_000, preview.PromotionDiscountMinor);
        Assert.Equal(2, preview.AppliedPromotions.Count);
        Assert.DoesNotContain(preview.AppliedPromotions, p => p.PromotionId == exclusiveId);

        var order = await CheckoutAsync(shopper);
        Assert.Equal(2_000, order.DiscountMinor);
        Assert.Equal(2, order.AppliedPromotionIds!.Count);
        AssertMoneyIdentity(order);
    }

    [Fact]
    public async Task A_threshold_that_is_not_met_grants_nothing_in_the_preview_or_the_charge()
    {
        const string currency = "QRR";
        var storefrontId = await SeedStorefrontAsync(currency, "Threshold Store");
        await SeedPromotionAsync(storefrontId, currency, "Free shipping over 500", grantsFreeShipping: true, minimumAmountMinor: 50_000);
        await SeedPromotionAsync(storefrontId, currency, "20% over 500", percentOff: 20, minimumAmountMinor: 50_000);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = Shopper(storefrontId);
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var preview = await SummaryAsync(shopper, storefrontId);
        // Nothing qualifies, so nothing can depend on shipping: the verdict is settled, not provisional.
        Assert.Equal(Settled, preview.Basis);
        Assert.Equal(0, preview.PromotionDiscountMinor);
        Assert.False(preview.FreeShippingApplied);
        Assert.Empty(preview.AppliedPromotions);

        var order = await CheckoutAsync(shopper, shippingMinor: 1_500);
        Assert.Equal(0, order.DiscountMinor);
        Assert.Equal(1_500, order.ShippingMinor);
        Assert.Empty(order.AppliedPromotionIds!);
        AssertMoneyIdentity(order);
    }

    // ---------------------------------------------------------------------------------------------
    // Cross-combinations: free shipping with a goods discount, and the store-wide setting
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Free_shipping_stacks_with_the_store_wide_discount_and_tax_follows_the_discounted_goods()
    {
        // The store-wide discount is a SETTING, not a promotion: it always applies and the combinable flag
        // never governs it. Free shipping waives the carrier rate; the two touch different money, so both
        // land — and the tax base follows the discounted goods with NO shipping in it.
        const string currency = "QSS";
        var storefrontId = await SeedStorefrontAsync(currency, "Free Ship + Store Discount", discountBps: 1_000, taxBps: 1_000);
        var freeShipId = await SeedPromotionAsync(storefrontId, currency, "Free shipping over 50", grantsFreeShipping: true, minimumAmountMinor: 5_000);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = Shopper(storefrontId);
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var preview = await SummaryAsync(shopper, storefrontId, shippingMinor: 1_500, shipToCountry: "DE");
        Assert.True(preview.FreeShippingApplied);
        Assert.Equal(1_000, preview.StorefrontDiscountMinor); // 10% of 10000, items only
        Assert.Equal(0, preview.PromotionDiscountMinor);
        Assert.Equal(9_000, preview.ItemsTotalMinor);

        var order = await CheckoutAsync(shopper, shippingMinor: 1_500);
        // discount 1000 (store-wide only) · shipping 0 (waived) · tax = 10% of (10000 − 1000 + 0) = 900
        Assert.Equal(10_000, order.NetMinor);
        Assert.Equal(1_000, order.DiscountMinor);
        Assert.Equal(0, order.ShippingMinor);
        Assert.Equal(900, order.TaxMinor);
        Assert.Equal(9_900, order.GrossMinor);
        Assert.Equal(freeShipId, Assert.Single(order.AppliedPromotionIds!));
        Assert.Equal(preview.ItemsTotalMinor, order.NetMinor - order.DiscountMinor);
        AssertMoneyIdentity(order);
        await SettleAsync(shopper, order);
    }

    [Fact]
    public async Task Free_shipping_stacks_with_a_product_scoped_threshold_discount()
    {
        // Two COMBINABLE promotions on different axes: a cart-wide free-shipping reward and a
        // product-scoped 10%. The product-scoped discount touches ONLY its own product's lines, so the
        // other line is untouched — and both are reported to the shopper.
        const string currency = "QTT";
        var storefrontId = await SeedStorefrontAsync(currency, "Free Ship + Product Store");
        var freeShipId = await SeedPromotionAsync(storefrontId, currency, "Free shipping over 50", grantsFreeShipping: true, minimumAmountMinor: 5_000, combinable: true);
        var scopedProductId = await fixture.SeedProductAsync(6_000, currency);
        var otherProductId = await fixture.SeedProductAsync(4_000, currency);
        var scopedId = await SeedPromotionAsync(
            storefrontId, currency, "10% off the widget", percentOff: 10, minimumQuantity: 1,
            combinable: true, scope: PromotionScopeKind.Product, productId: scopedProductId);

        using var shopper = Shopper(storefrontId);
        await shopper.PostAsJsonAsync("/cart/items", new { productId = scopedProductId, quantity = 1 });
        await shopper.PostAsJsonAsync("/cart/items", new { productId = otherProductId, quantity = 1 });

        var preview = await SummaryAsync(shopper, storefrontId, shippingMinor: 1_500, shipToCountry: "DE");
        Assert.True(preview.FreeShippingApplied);
        Assert.Equal(600, preview.PromotionDiscountMinor); // 10% of the 6000 line only, not of 10000
        Assert.Equal(2, preview.AppliedPromotions.Count);
        Assert.Equal(600, preview.AppliedPromotions.Single(p => p.PromotionId == scopedId).DiscountMinor);
        Assert.Equal(0, preview.AppliedPromotions.Single(p => p.PromotionId == freeShipId).DiscountMinor);

        var order = await CheckoutAsync(shopper, shippingMinor: 1_500);
        Assert.Equal(10_000, order.NetMinor);
        Assert.Equal(600, order.DiscountMinor);
        Assert.Equal(0, order.ShippingMinor);
        Assert.Equal(9_400, order.GrossMinor);
        Assert.Equal(2, order.AppliedPromotionIds!.Count);
        AssertMoneyIdentity(order);
        await SettleAsync(shopper, order);
    }

    [Fact]
    public async Task A_coupon_free_shipping_and_the_store_wide_discount_all_apply_together()
    {
        // Everything at once (ADR-0052 + ADR-0051 + ADR-0053): a code-gated COMBINABLE 10% coupon, a
        // combinable free-shipping promotion, and the store's own 5% setting. A coupon is just a
        // promotion once unlocked, so the Combinable flag governs it exactly like the rest.
        const string currency = "QUU";
        var storefrontId = await SeedStorefrontAsync(currency, "Everything Store", discountBps: 500, taxBps: 1_000);
        var freeShipId = await SeedPromotionAsync(storefrontId, currency, "Free shipping over 50", grantsFreeShipping: true, minimumAmountMinor: 5_000, combinable: true);
        var couponId = await SeedPromotionAsync(storefrontId, currency, "TENOFF", percentOff: 10, minimumAmountMinor: 5_000, combinable: true, code: "TENOFF");
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = Shopper(storefrontId);
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        // Without the code, only the free-shipping promotion is in play.
        var withoutCode = await SummaryAsync(shopper, storefrontId, shippingMinor: 1_500, shipToCountry: "DE");
        Assert.Equal(0, withoutCode.PromotionDiscountMinor);
        Assert.Equal(freeShipId, Assert.Single(withoutCode.AppliedPromotions).PromotionId);

        var preview = await SummaryAsync(shopper, storefrontId, shippingMinor: 1_500, shipToCountry: "DE", couponCode: "TENOFF");
        Assert.True(preview.FreeShippingApplied);
        Assert.Equal(1_000, preview.PromotionDiscountMinor);
        Assert.Equal(500, preview.StorefrontDiscountMinor);
        Assert.Equal(8_500, preview.ItemsTotalMinor);
        Assert.Contains(preview.AppliedPromotions, p => p.PromotionId == couponId);

        var order = await CheckoutAsync(shopper, shippingMinor: 1_500, couponCode: "TENOFF");
        // discount = 1000 coupon + 500 store-wide = 1500 · shipping 0 · tax 10% of 8500 = 850
        Assert.Equal(10_000, order.NetMinor);
        Assert.Equal(1_500, order.DiscountMinor);
        Assert.Equal(0, order.ShippingMinor);
        Assert.Equal(850, order.TaxMinor);
        Assert.Equal(9_350, order.GrossMinor);
        Assert.Equal("TENOFF", order.CouponCode);
        Assert.Equal(preview.ItemsTotalMinor, order.NetMinor - order.DiscountMinor);
        AssertMoneyIdentity(order);
        await SettleAsync(shopper, order);
    }

    [Fact]
    public async Task A_coupon_racing_free_shipping_is_decided_on_the_quoted_rate_not_a_guess()
    {
        // The coupon variant of the headline divergence: an EXCLUSIVE coupon worth 900 against a
        // free-shipping promotion worth the carrier rate. At 1500 free shipping wins and the coupon is
        // NOT redeemed; the preview says the same thing, on the same rate.
        const string currency = "QVV";
        var storefrontId = await SeedStorefrontAsync(currency, "Coupon Race Store");
        var freeShipId = await SeedPromotionAsync(storefrontId, currency, "Free shipping over 50", grantsFreeShipping: true, minimumAmountMinor: 5_000);
        await SeedPromotionAsync(storefrontId, currency, "NINEOFF", discountAmountMinor: 900, minimumAmountMinor: 5_000, code: "NINEOFF");
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = Shopper(storefrontId);
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var preview = await SummaryAsync(shopper, storefrontId, shippingMinor: 1_500, shipToCountry: "DE", couponCode: "NINEOFF");
        Assert.True(preview.FreeShippingApplied);
        Assert.Equal(0, preview.PromotionDiscountMinor);
        Assert.Equal(freeShipId, Assert.Single(preview.AppliedPromotions).PromotionId);

        var order = await CheckoutAsync(shopper, shippingMinor: 1_500, couponCode: "NINEOFF");
        Assert.True(order.FreeShippingApplied);
        Assert.Equal(0, order.DiscountMinor);
        Assert.Equal(freeShipId, Assert.Single(order.AppliedPromotionIds!));
        // The coupon lost the contest, so no allowance was burned (ADR-0052).
        Assert.Null(order.CouponCode);
        AssertMoneyIdentity(order);
        await SettleAsync(shopper, order);
    }

    [Fact]
    public async Task A_stack_deeper_than_the_cart_is_capped_at_the_subtotal_and_allocates_exactly()
    {
        // Three combinable 50%-off promotions want 150% of the cart. The stack is capped at the subtotal,
        // so the goods land at exactly zero (never negative) — and with free shipping also in the winning
        // set, the order is genuinely free. Preview and charge must agree on all of it.
        const string currency = "QWW";
        var storefrontId = await SeedStorefrontAsync(currency, "Capped Store");
        for (var i = 0; i < 3; i++)
        {
            await SeedPromotionAsync(storefrontId, currency, $"Half off {i}", percentOff: 50, minimumAmountMinor: 1_000, combinable: true);
        }

        await SeedPromotionAsync(storefrontId, currency, "Free shipping", grantsFreeShipping: true, minimumAmountMinor: 1_000, combinable: true);
        var productId = await fixture.SeedProductAsync(3_333, currency);

        using var shopper = Shopper(storefrontId);
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 3 });

        var preview = await SummaryAsync(shopper, storefrontId, shippingMinor: 1_500, shipToCountry: "DE");
        Assert.Equal(9_999, preview.SubtotalMinor);
        Assert.Equal(9_999, preview.PromotionDiscountMinor); // capped at the subtotal, not 14998
        Assert.Equal(0, preview.ItemsTotalMinor);

        var order = await CheckoutAsync(shopper, shippingMinor: 1_500);
        Assert.Equal(9_999, order.NetMinor);
        Assert.Equal(9_999, order.DiscountMinor);
        Assert.Equal(0, order.ShippingMinor);
        Assert.Equal(0, order.GrossMinor);
        AssertMoneyIdentity(order);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The money identity every charge must satisfy: NetMinor is the PRE-discount subtotal, so the
    /// shopper pays <c>Net − Discount + Ship + Tax</c>. Also pins the two invariants a promotion could
    /// break: the discount never exceeds the goods, and gross never goes negative.
    /// </summary>
    private static void AssertMoneyIdentity(CheckoutResponseDto order)
    {
        Assert.Equal(order.GrossMinor, order.NetMinor - order.DiscountMinor + order.ShippingMinor + order.TaxMinor);
        Assert.InRange(order.DiscountMinor, 0, order.NetMinor);
        Assert.True(order.GrossMinor >= 0);
    }

    private HttpClient Shopper(Guid storefrontId)
    {
        var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
        return client;
    }

    private static async Task<SummaryDto> SummaryAsync(
        HttpClient shopper, Guid storefrontId, long? shippingMinor = null, string? shipToCountry = null, string? couponCode = null)
    {
        var query = $"?storefrontId={storefrontId}";
        if (shippingMinor is { } rate)
        {
            query += $"&shippingMinor={rate}";
        }

        if (shipToCountry is not null)
        {
            query += $"&shipToCountry={shipToCountry}";
        }

        if (couponCode is not null)
        {
            query += $"&couponCode={couponCode}";
        }

        var response = await shopper.GetAsync($"/cart/summary{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SummaryDto>())!;
    }

    private static async Task<CheckoutResponseDto> CheckoutAsync(
        HttpClient shopper, long? shippingMinor = null, string? couponCode = null)
    {
        object body = shippingMinor is { } rate
            ? new
            {
                email = "buyer@example.com",
                shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
                selectedShippingService = "fake-standard",
                selectedShippingAmountMinor = rate,
                selectedShippingExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
                couponCode,
            }
            : new
            {
                email = "buyer@example.com",
                shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
                couponCode,
            };

        var response = await shopper.PostAsJsonAsync("/checkout", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
    }

    /// <summary>Pays the order and asserts the ledger still nets to zero (NFR-1).</summary>
    private async Task SettleAsync(HttpClient shopper, CheckoutResponseDto order)
    {
        await WaitForSagaAsync(order.OrderId);
        using var payments = fixture.Payments.CreateClient();
        var result = await payments.PostAsync($"/dev/simulate-payment/pi_fake_{order.OrderId:N}?amountMinor={order.GrossMinor}", null);
        result.EnsureSuccessStatusCode();
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    private async Task<Guid> SeedStorefrontAsync(string currency, string name, int discountBps = 0, int taxBps = 0)
    {
        var storefrontId = Guid.CreateVersion7();
        await fixture.PublishAsync(new StorefrontConfigChanged(
            storefrontId, TenantId, name, currency, taxBps, IsLive: true, TaxInclusive: false, DiscountBps: discountBps));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var copy = await db.StorefrontTaxCopies.AsNoTracking().FirstOrDefaultAsync(c => c.StorefrontId == storefrontId);
            if (copy is { IsLive: true } && copy.DiscountBasisPoints == discountBps)
            {
                return storefrontId;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Storefront copy {storefrontId} did not project.");
    }

    private async Task<Guid> SeedPromotionAsync(
        Guid storefrontId, string currency, string name,
        long minimumAmountMinor = 0, int minimumQuantity = 0, bool grantsFreeShipping = false,
        int percentOff = 0, long discountAmountMinor = 0, bool combinable = false,
        PromotionScopeKind scope = PromotionScopeKind.Storefront, Guid? productId = null, string? code = null)
    {
        var promotionId = Guid.CreateVersion7();
        await fixture.PublishAsync(new PromotionChanged(
            promotionId, TenantId, storefrontId, name, currency, scope, productId,
            minimumAmountMinor, minimumQuantity, grantsFreeShipping, percentOff, discountAmountMinor,
            combinable, Active: true, ActiveFrom: null, ActiveUntil: null,
            Code: code, MaxRedemptions: null, MaxRedemptionsPerCustomer: null));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope2 = fixture.Ordering.Services.CreateScope();
            var db = scope2.ServiceProvider.GetRequiredService<OrderingDbContext>();
            if (await db.PromotionCopies.AsNoTracking().AnyAsync(p => p.PromotionId == promotionId))
            {
                return promotionId;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"PromotionCopy {promotionId} did not project.");
    }

    private async Task WaitForSagaAsync(Guid orderId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            if (await db.CheckoutStates.AsNoTracking().AnyAsync(s => s.CorrelationId == orderId))
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Checkout saga for {orderId} did not start.");
    }

    private static async Task WaitForStatusAsync(HttpClient client, Guid orderId, string expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<StatusDto>($"/orders/{orderId}/status");
            if (status?.Status == expected)
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Order {orderId} never reached {expected}.");
    }

    private sealed record StatusDto(Guid Id, string Status);
}
