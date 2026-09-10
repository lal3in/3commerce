using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// What a coupon allowance may and may not be spent on, and what checkout actually WRITES DOWN about a
/// discount (rev_lock / rev_zero / rev_alloc). Three defects the pricing audit found at the seams, each
/// reproduced here before it was fixed:
/// <list type="bullet">
/// <item>a per-customer hold stranded by a crash locked that shopper out of the coupon FOREVER, because
/// only the GLOBAL cap ever swept stale holds;</item>
/// <item>a reward worth nothing — free shipping on a cart that pays no shipping — still burned a
/// single-use allowance;</item>
/// <item>nothing asserted the per-line discount checkout PERSISTS, so the allocation could have been
/// garbage and every existing test would still have passed.</item>
/// </list>
/// <para>
/// Each test owns a unique three-letter currency so its storefront and promotions can never be picked up
/// by another test's cart.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class CouponAllowanceTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private sealed record CheckoutResponseDto(
        Guid OrderId, string ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
        long GrossMinor, string Currency, string? Message, bool FreeShippingApplied = false,
        List<Guid>? AppliedPromotionIds = null, string? CouponCode = null);

    [Fact]
    public async Task A_hold_stranded_by_a_crash_does_not_lock_the_shopper_out_of_the_coupon_forever()
    {
        // rev_lock. A reservation committed, then the process died before the checkout attempt committed:
        // a Reserved row pointing at an order that does not exist and never will. The per-customer count
        // includes it, so this shopper is refused — and NOTHING used to be able to give it back, because
        // the stale sweep only ran when the GLOBAL counter looked exhausted. One crash, one shopper
        // permanently locked out of a coupon that is not even close to its cap.
        const string currency = "QZ1";
        const string email = "stranded@example.com";
        var storefrontId = await LiveStorefrontAsync(currency);
        var promotionId = await CouponAsync(storefrontId, currency, "ONCEEACH", percentOff: 10, maxRedemptionsPerCustomer: 1);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        var strandedOrderId = await StrandHoldAsync(promotionId, $"e:{email}", TimeSpan.FromMinutes(90));
        Assert.Equal(1, await HeldRedemptionsAsync(promotionId));

        using var shopper = NewShopper(storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper, coupon: "ONCEEACH", email: email);

        // The shopper gets their one redemption: the crash residue was reclaimed, not counted against them.
        Assert.Equal(1_000, order.DiscountMinor);
        Assert.Equal("ONCEEACH", order.CouponCode);
        AssertMoneyIdentity(order);

        Assert.Equal(PromotionRedemptionStatus.Released, await StatusOfAsync(strandedOrderId));
        Assert.Equal(1, await HeldRedemptionsAsync(promotionId)); // the real one, not the ghost
    }

    [Fact]
    public async Task A_stale_hold_still_counts_until_it_is_actually_stale()
    {
        // The other side of rev_lock: the sweep must not become a way around the limit. A hold taken
        // MINUTES ago is a shopper mid-checkout, not crash residue, and it still refuses the second try.
        const string currency = "QZ2";
        const string email = "inflight@example.com";
        var storefrontId = await LiveStorefrontAsync(currency);
        await CouponAsync(storefrontId, currency, "ONCEONLY", percentOff: 10, maxRedemptionsPerCustomer: 1);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var first = NewShopper(storefrontId);
        (await first.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(first, coupon: "ONCEONLY", email: email);
        Assert.Equal("ONCEONLY", order.CouponCode);

        using var second = NewShopper(storefrontId);
        (await second.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var refused = await second.PostAsJsonAsync("/checkout", CheckoutBody(email, "ONCEONLY"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task A_reward_worth_nothing_does_not_burn_a_single_use_allowance()
    {
        // rev_zero. A free-shipping code on an ALL-DIGITAL cart: there is no shipping to waive, so the
        // reward is worth exactly 0. It still "wins" its comparison (nothing competes with it), and
        // checkout used to reserve it — spending a one-shot allowance on a shopper who saved nothing.
        const string currency = "QZ3";
        var storefrontId = await LiveStorefrontAsync(currency);
        var promotionId = await CouponAsync(
            storefrontId, currency, "FREESHIP1", percentOff: 0, maxRedemptions: 1, freeShipping: true);
        var (digitalProductId, _) = await fixture.SeedSuppliedProductAsync(
            priceMinor: 10_000, supplierCostMinor: 0, currency: currency, fulfilmentType: FulfilmentType.DigitalDownload);

        using var digitalShopper = NewShopper(storefrontId);
        (await digitalShopper.PostAsJsonAsync("/cart/items", new { productId = digitalProductId, quantity = 1 }))
            .EnsureSuccessStatusCode();
        var digital = await CheckoutAsync(digitalShopper, coupon: "FREESHIP1", email: "digital@example.com");

        // Nothing was saved, so nothing was spent — and the order does not claim a coupon it never used.
        Assert.Equal(0, digital.ShippingMinor);
        Assert.Equal(0, digital.DiscountMinor);
        Assert.Null(digital.CouponCode);
        AssertMoneyIdentity(digital);
        Assert.Equal(0, await RedeemedCountAsync(promotionId));
        Assert.Equal(0, await HeldRedemptionsAsync(promotionId));

        // Proof the allowance survived: the next shopper — with a cart that DOES pay shipping — still
        // gets the code, which is exactly what the first shopper would have stolen.
        var shippableProductId = await fixture.SeedProductAsync(10_000, currency);
        using var shippableShopper = NewShopper(storefrontId);
        (await shippableShopper.PostAsJsonAsync("/cart/items", new { productId = shippableProductId, quantity = 1 }))
            .EnsureSuccessStatusCode();
        var shippable = await CheckoutAsync(shippableShopper, coupon: "FREESHIP1", email: "shippable@example.com");

        Assert.True(shippable.FreeShippingApplied);
        Assert.Equal(0, shippable.ShippingMinor);
        Assert.Equal("FREESHIP1", shippable.CouponCode);
        Assert.Equal(1, await RedeemedCountAsync(promotionId));
    }

    [Fact]
    public async Task The_persisted_per_line_discount_is_the_allocation_the_shopper_was_charged()
    {
        // rev_alloc. Checkout WRITES a per-line discount, and that number is what a refund later reads to
        // decide what a returned line was actually sold for (ADR-0055). Nothing asserted it: the response
        // totals could be perfect while the persisted vector was nonsense, and the whole suite stayed
        // green. This reads the rows back out of the database.
        const string currency = "QZ4";
        var storefrontId = await LiveStorefrontAsync(currency);
        var discounted = await fixture.SeedProductAsync(10_000, currency);
        var untouched = await fixture.SeedProductAsync(6_000, currency);
        var promotionId = await ProductPromotionAsync(storefrontId, currency, discounted, percentOff: 10);

        using var shopper = NewShopper(storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = discounted, quantity = 1 })).EnsureSuccessStatusCode();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = untouched, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper, email: "alloc@example.com");

        Assert.Equal(16_000, order.NetMinor);
        Assert.Equal(1_000, order.DiscountMinor);
        Assert.Equal([promotionId], order.AppliedPromotionIds);
        AssertMoneyIdentity(order);

        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var attempt = await db.CheckoutAttempts.AsNoTracking()
            .Include(a => a.Lines)
            .FirstAsync(a => a.Id == order.OrderId);

        // The promotion covered ONE line, so the whole allocation sits on it and the other line is
        // untouched — a product-scoped discount must not smear across the cart.
        var discountedLine = attempt.Lines.Single(l => l.ProductId == discounted);
        var untouchedLine = attempt.Lines.Single(l => l.ProductId == untouched);
        Assert.Equal(1_000, discountedLine.DiscountMinor);
        Assert.Equal(0, untouchedLine.DiscountMinor);

        // The vector sums to the PROMOTION share exactly — the invariant the refund basis depends on.
        Assert.Equal(attempt.PromotionDiscountMinor, attempt.Lines.Sum(l => l.DiscountMinor));
        Assert.Equal(order.DiscountMinor, attempt.DiscountMinor);
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static void AssertMoneyIdentity(CheckoutResponseDto order) =>
        Assert.Equal(order.GrossMinor, order.NetMinor - order.DiscountMinor + order.ShippingMinor + order.TaxMinor);

    private HttpClient NewShopper(Guid storefrontId)
    {
        var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
        return client;
    }

    private static object CheckoutBody(string email, string? coupon) => new
    {
        email,
        shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
        couponCode = coupon,
    };

    private static async Task<CheckoutResponseDto> CheckoutAsync(
        HttpClient client, string? coupon = null, string email = "buyer@example.com")
    {
        var response = await client.PostAsJsonAsync("/checkout", CheckoutBody(email, coupon));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
    }

    /// <summary>
    /// Writes the residue of a crash: a Reserved redemption whose order never materialized, aged past the
    /// 45-minute stale threshold, with the promotion counter incremented exactly as the reservation would
    /// have left it. Returns the phantom order id.
    /// </summary>
    private async Task<Guid> StrandHoldAsync(Guid promotionId, string customerKey, TimeSpan age)
    {
        var orderId = Guid.CreateVersion7();
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        db.PromotionRedemptions.Add(new PromotionRedemption
        {
            Id = Guid.CreateVersion7(),
            PromotionId = promotionId,
            TenantId = TenantId,
            OrderId = orderId,
            CustomerKey = customerKey,
            Code = string.Empty,
            Status = PromotionRedemptionStatus.Reserved,
            ReservedAt = DateTimeOffset.UtcNow - age,
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE ordering."PromotionCopies" SET "RedeemedCount" = "RedeemedCount" + 1 WHERE "PromotionId" = {promotionId}""");
        return orderId;
    }

    private async Task<PromotionRedemptionStatus?> StatusOfAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return (await db.PromotionRedemptions.AsNoTracking().FirstOrDefaultAsync(r => r.OrderId == orderId))?.Status;
    }

    private async Task<int> HeldRedemptionsAsync(Guid promotionId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return await db.PromotionRedemptions.AsNoTracking()
            .CountAsync(r => r.PromotionId == promotionId && r.Status != PromotionRedemptionStatus.Released);
    }

    private async Task<int> RedeemedCountAsync(Guid promotionId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return (await db.PromotionCopies.AsNoTracking().FirstAsync(p => p.PromotionId == promotionId)).RedeemedCount;
    }

    private async Task<Guid> LiveStorefrontAsync(string currency)
    {
        var storefrontId = Guid.CreateVersion7();
        await fixture.PublishAsync(new StorefrontConfigChanged(
            storefrontId, TenantId, $"Allowance Store {currency}", currency, 0, IsLive: true, TaxInclusive: false, DiscountBps: 0));
        await WaitAsync(async db => await db.StorefrontTaxCopies.AsNoTracking()
            .AnyAsync(c => c.StorefrontId == storefrontId && c.IsLive), $"Storefront {storefrontId}");
        return storefrontId;
    }

    private async Task<Guid> CouponAsync(
        Guid storefrontId, string currency, string code, int percentOff,
        int? maxRedemptions = null, int? maxRedemptionsPerCustomer = null, bool freeShipping = false)
    {
        var promotionId = Guid.CreateVersion7();
        await fixture.PublishAsync(new PromotionChanged(
            promotionId, TenantId, storefrontId, $"Coupon {code}", currency,
            PromotionScopeKind.Storefront, ProductId: null,
            MinimumAmountMinor: 0, MinimumQuantity: 0,
            GrantsFreeShipping: freeShipping, PercentOff: percentOff, DiscountAmountMinor: 0,
            Combinable: false, Active: true, ActiveFrom: null, ActiveUntil: null,
            Code: code, MaxRedemptions: maxRedemptions, MaxRedemptionsPerCustomer: maxRedemptionsPerCustomer));
        await WaitAsync(async db => (await db.PromotionCopies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PromotionId == promotionId))?.Code == code, $"Coupon {code}");
        return promotionId;
    }

    /// <summary>An uncoded promotion scoped to ONE product, so its allocation must land on that line only.</summary>
    private async Task<Guid> ProductPromotionAsync(Guid storefrontId, string currency, Guid productId, int percentOff)
    {
        var promotionId = Guid.CreateVersion7();
        await fixture.PublishAsync(new PromotionChanged(
            promotionId, TenantId, storefrontId, "Product promo", currency,
            PromotionScopeKind.Product, ProductId: productId,
            MinimumAmountMinor: 0, MinimumQuantity: 0,
            GrantsFreeShipping: false, PercentOff: percentOff, DiscountAmountMinor: 0,
            Combinable: true, Active: true, ActiveFrom: null, ActiveUntil: null,
            Code: null, MaxRedemptions: null, MaxRedemptionsPerCustomer: null));
        await WaitAsync(async db => await db.PromotionCopies.AsNoTracking()
            .AnyAsync(p => p.PromotionId == promotionId), $"Promotion {promotionId}");
        return promotionId;
    }

    private async Task WaitAsync(Func<OrderingDbContext, Task<bool>> projected, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            if (await projected(db))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"{what} did not project.");
    }
}
