using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// The three coverage gaps the pricing audit named, each pinning a rule that was implemented but never
/// proven end to end:
/// <list type="bullet">
/// <item>the per-customer limit under CONCURRENT checkouts — the only read-then-write window in the
/// redemption path, guarded by an advisory lock rather than by a conditional UPDATE, and therefore the
/// one place where the cap could in principle be beaten;</item>
/// <item>a storefront-SCOPED promotion at integration level: it must discount its own store and no other,
/// while an all-storefront promotion reaches every store of its currency;</item>
/// <item>the JOINT cap — a storefront-wide discount and a promotion together can never take more than the
/// goods are worth.</item>
/// </list>
/// <para>Each test owns a unique three-letter currency so no other test's cart can see its promotions.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class PromotionScopeAndCapTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private sealed record CheckoutResponseDto(
        Guid OrderId, string ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
        long GrossMinor, string Currency, string? Message, bool FreeShippingApplied = false,
        List<Guid>? AppliedPromotionIds = null, string? CouponCode = null);

    [Fact]
    public async Task The_per_customer_limit_holds_under_concurrent_checkouts()
    {
        // THE untested race. The total cap is race-safe by construction — one conditional UPDATE whose
        // rows-affected is the verdict, serialized by Postgres on that row. The per-customer limit has no
        // such row to serialize on: it COUNTS and then INSERTS, so without the transaction-scoped advisory
        // lock on (promotion, customer) eight simultaneous checkouts would each read "0 used" and each
        // take a redemption. Same shopper, same code, all at once.
        const string currency = "QP1";
        const string email = "sameperson@example.com";
        const int shoppers = 8;
        var storefrontId = await LiveStorefrontAsync(currency, discountBps: 0);
        var promotionId = await CouponAsync(storefrontId, currency, "ONEEACH", maxRedemptionsPerCustomer: 1);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        var clients = new List<HttpClient>();
        try
        {
            foreach (var _ in Enumerable.Range(0, shoppers))
            {
                var client = NewShopper(storefrontId);
                clients.Add(client);
                (await client.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
            }

            // The SAME email on every request, so only the per-customer limit can refuse them.
            var responses = await Task.WhenAll(clients.Select(c =>
                c.PostAsJsonAsync("/checkout", CheckoutBody(email, "ONEEACH"))));

            var accepted = responses.Count(r => r.IsSuccessStatusCode);
            Assert.Equal(1, accepted);
            Assert.Equal(shoppers - 1, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));

            // The rows agree with the verdict: exactly one hold exists, and the counter was never
            // overshot and corrected — it only ever reached 1.
            Assert.Equal(1, await HeldRedemptionsAsync(promotionId));
            Assert.Equal(1, await RedeemedCountAsync(promotionId));

            foreach (var r in responses)
            {
                r.Dispose();
            }
        }
        finally
        {
            foreach (var c in clients)
            {
                c.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_storefront_scoped_promotion_discounts_its_own_store_and_no_other()
    {
        // Two live storefronts of the SAME tenant and the SAME currency — the arrangement that made the
        // tax bug (ADR-0055) possible. A promotion scoped to one of them must not leak into the other.
        const string currency = "QP2";
        var mine = await LiveStorefrontAsync(currency, discountBps: 0);
        var theirs = await LiveStorefrontAsync(currency, discountBps: 0);
        var productId = await fixture.SeedProductAsync(10_000, currency);
        var promotionId = await PromotionAsync(currency, percentOff: 10, storefrontId: mine);

        var onMine = await CheckoutOnAsync(mine, productId, "mine@example.com");
        Assert.Equal(1_000, onMine.DiscountMinor);
        Assert.Equal([promotionId], onMine.AppliedPromotionIds);

        var onTheirs = await CheckoutOnAsync(theirs, productId, "theirs@example.com");
        Assert.Equal(0, onTheirs.DiscountMinor);
        Assert.Empty(onTheirs.AppliedPromotionIds ?? []);

        // The order records the promotion's NAME as well as its id (promo_names). An id alone stops
        // explaining a historical charge the moment the promotion is renamed or deleted, and "why was this
        // discounted?" is exactly what support asks months later.
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var attempt = await db.CheckoutAttempts.AsNoTracking().FirstAsync(a => a.Id == onMine.OrderId);
        Assert.Equal(promotionId.ToString(), attempt.AppliedPromotionIds);
        Assert.Equal("Scoped promo", attempt.AppliedPromotionNames);

        var unpromoted = await db.CheckoutAttempts.AsNoTracking().FirstAsync(a => a.Id == onTheirs.OrderId);
        Assert.Null(unpromoted.AppliedPromotionNames);
    }

    [Fact]
    public async Task An_all_storefront_promotion_reaches_every_store_of_its_currency()
    {
        // The other half of the scope rule: a promotion with NO storefront is deliberately tenant-wide
        // within its currency, so both stores discount. Without this the test above would also pass if
        // promotions simply never applied.
        const string currency = "QP3";
        var first = await LiveStorefrontAsync(currency, discountBps: 0);
        var second = await LiveStorefrontAsync(currency, discountBps: 0);
        var productId = await fixture.SeedProductAsync(10_000, currency);
        var promotionId = await PromotionAsync(currency, percentOff: 10, storefrontId: null);

        var onFirst = await CheckoutOnAsync(first, productId, "first@example.com");
        var onSecond = await CheckoutOnAsync(second, productId, "second@example.com");

        Assert.Equal(1_000, onFirst.DiscountMinor);
        Assert.Equal(1_000, onSecond.DiscountMinor);
        Assert.Equal([promotionId], onSecond.AppliedPromotionIds);
    }

    [Fact]
    public async Task A_storefront_discount_and_a_promotion_are_jointly_capped_at_the_subtotal()
    {
        // Two independent deductions that do not know about each other: a store-wide 60% and a promotion
        // taking 60% more. Together they want 120% of the goods. The joint clamp at the subtotal is what
        // stops the order going negative — and the money identity and the ledger must survive the edge.
        const string currency = "QP4";
        var storefrontId = await LiveStorefrontAsync(currency, discountBps: 6_000);
        var productId = await fixture.SeedProductAsync(10_000, currency);
        await PromotionAsync(currency, percentOff: 60, storefrontId: storefrontId);

        var order = await CheckoutOnAsync(storefrontId, productId, "capped@example.com");

        // Goods are free, not negative: the discount is exactly the subtotal, never more.
        Assert.Equal(10_000, order.NetMinor);
        Assert.Equal(10_000, order.DiscountMinor);
        Assert.Equal(order.GrossMinor, order.NetMinor - order.DiscountMinor + order.ShippingMinor + order.TaxMinor);

        // Shipping is NEVER discounted by an items discount, so the shopper still pays it.
        Assert.Equal(499, order.ShippingMinor);
        Assert.Equal(499, order.GrossMinor);

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(storefrontId, order.OrderId, "Confirmed");
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

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

    private async Task<CheckoutResponseDto> CheckoutOnAsync(Guid storefrontId, Guid productId, string email)
    {
        using var client = NewShopper(storefrontId);
        (await client.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var response = await client.PostAsJsonAsync("/checkout", CheckoutBody(email, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
    }

    private async Task SimulatePaymentAsync(Guid orderId, long gross)
    {
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.CheckoutStates.AsNoTracking().AnyAsync(s => s.CorrelationId == orderId);
        }, $"Checkout saga for {orderId} did not start");

        using var payments = fixture.Payments.CreateClient();
        (await payments.PostAsync($"/dev/simulate-payment/pi_fake_{orderId:N}?amountMinor={gross}", null))
            .EnsureSuccessStatusCode();
    }

    private async Task WaitForStatusAsync(Guid storefrontId, Guid orderId, string expected)
    {
        using var client = NewShopper(storefrontId);
        await WaitAsync(async () =>
        {
            var status = await client.GetFromJsonAsync<StatusDto>($"/orders/{orderId}/status");
            return status?.Status == expected;
        }, $"Order {orderId} did not reach {expected}");
    }

    private sealed record StatusDto(Guid Id, string Status);

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

    private async Task<Guid> LiveStorefrontAsync(string currency, int discountBps)
    {
        var storefrontId = Guid.CreateVersion7();
        await fixture.PublishAsync(new StorefrontConfigChanged(
            storefrontId, TenantId, $"Scope Store {currency}", currency, 0,
            IsLive: true, TaxInclusive: false, DiscountBps: discountBps));
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.StorefrontTaxCopies.AsNoTracking()
                .AnyAsync(c => c.StorefrontId == storefrontId && c.IsLive && c.DiscountBasisPoints == discountBps);
        }, $"Storefront {storefrontId} did not project");
        return storefrontId;
    }

    /// <summary>An automatic (uncoded) promotion; null storefrontId = every store of this currency.</summary>
    private async Task<Guid> PromotionAsync(string currency, int percentOff, Guid? storefrontId)
    {
        var promotionId = Guid.CreateVersion7();
        await fixture.PublishAsync(new PromotionChanged(
            promotionId, TenantId, storefrontId, "Scoped promo", currency,
            PromotionScopeKind.Storefront, ProductId: null,
            MinimumAmountMinor: 0, MinimumQuantity: 0,
            GrantsFreeShipping: false, PercentOff: percentOff, DiscountAmountMinor: 0,
            Combinable: true, Active: true, ActiveFrom: null, ActiveUntil: null,
            Code: null, MaxRedemptions: null, MaxRedemptionsPerCustomer: null));
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.PromotionCopies.AsNoTracking().AnyAsync(p => p.PromotionId == promotionId);
        }, $"Promotion {promotionId} did not project");
        return promotionId;
    }

    private async Task<Guid> CouponAsync(Guid storefrontId, string currency, string code, int? maxRedemptionsPerCustomer)
    {
        var promotionId = Guid.CreateVersion7();
        await fixture.PublishAsync(new PromotionChanged(
            promotionId, TenantId, storefrontId, $"Coupon {code}", currency,
            PromotionScopeKind.Storefront, ProductId: null,
            MinimumAmountMinor: 0, MinimumQuantity: 0,
            GrantsFreeShipping: false, PercentOff: 10, DiscountAmountMinor: 0,
            Combinable: false, Active: true, ActiveFrom: null, ActiveUntil: null,
            Code: code, MaxRedemptions: null, MaxRedemptionsPerCustomer: maxRedemptionsPerCustomer));
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return (await db.PromotionCopies.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PromotionId == promotionId))?.Code == code;
        }, $"Coupon {code} did not project");
        return promotionId;
    }

    private static async Task WaitAsync(Func<Task<bool>> settled, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await settled())
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"{what}.");
    }
}
