using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Coupon codes end to end (ADR-0052): a code-gated promotion that is CHARGED at checkout, rationed by a
/// race-safe reservation, released when the checkout dies, and confirmed exactly once when the order
/// lands. Every money case asserts Net − Discount + Ship + Tax = Gross and a ZERO trial balance — a
/// coupon adds no ledger line, it only lowers the charged gross.
/// <para>
/// Each test owns a unique three-letter currency so its live storefront + promotion pair can never be
/// picked up by another test's cart (the same isolation trick the threshold-promotion cases use).
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class CouponRedemptionTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    // CheckoutResponse is a positional record whose coupon field is APPENDED with a default (ADR-0052).
    private sealed record CheckoutResponseDto(
        Guid OrderId, string ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
        long GrossMinor, string Currency, string? Message, bool FreeShippingApplied = false,
        List<Guid>? AppliedPromotionIds = null, string? CouponCode = null);

    private sealed record CartSummaryDto(
        long SubtotalMinor, long StorefrontDiscountMinor, long PromotionDiscountMinor, long ItemsTotalMinor,
        bool FreeShippingApplied, List<AppliedPromotionDto> AppliedPromotions, string Currency,
        int CouponStatus = 0, string? CouponCode = null, string CouponPromotionName = "");

    private sealed record AppliedPromotionDto(Guid PromotionId, string Name, long DiscountMinor);

    private sealed record StatusDto(Guid Id, string Status);

    [Fact]
    public async Task A_coupon_code_is_required_for_the_discount_and_is_actually_charged()
    {
        // The SAME promotion, twice: without the code it is invisible; with it, the goods are discounted
        // and the charge (and the ledger) follow. This is the whole feature in one assertion pair.
        const string currency = "QK1";
        var storefrontId = await LiveStorefrontAsync(currency);
        var promotionId = await CouponAsync(storefrontId, currency, "WELCOME10", percentOff: 10);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var noCode = NewShopper(storefrontId);
        (await noCode.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var full = await CheckoutAsync(noCode);
        Assert.Equal(0, full.DiscountMinor);
        Assert.Empty(full.AppliedPromotionIds ?? []);
        Assert.Null(full.CouponCode);

        using var withCode = NewShopper(storefrontId);
        (await withCode.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var discounted = await CheckoutAsync(withCode, coupon: "welcome10"); // typed in lowercase on purpose

        Assert.Equal(10_000, discounted.NetMinor);
        Assert.Equal(1_000, discounted.DiscountMinor);
        Assert.Equal([promotionId], discounted.AppliedPromotionIds);
        Assert.Equal("WELCOME10", discounted.CouponCode);
        Assert.Equal(9_499, discounted.GrossMinor); // 10000 − 1000 + 499 shipping + 0 tax
        AssertMoneyIdentity(discounted);

        await SimulatePaymentAsync(discounted.OrderId, discounted.GrossMinor);
        await WaitForStatusAsync(withCode, discounted.OrderId, "Confirmed");
        Assert.Equal(0, await fixture.TrialBalanceAsync());

        // Confirmed, not merely reserved: the coupon is spent for good.
        Assert.Equal(PromotionRedemptionStatus.Confirmed, await RedemptionStatusAsync(discounted.OrderId));
    }

    [Fact]
    public async Task The_cap_holds_under_concurrent_checkouts()
    {
        // THE race. Ten shoppers check out simultaneously against a coupon with MaxRedemptions = 3.
        // The cap is enforced by ONE conditional UPDATE whose rows-affected is the answer, so Postgres
        // serializes the ten claims on that row: exactly three can win, no matter the interleaving.
        const string currency = "QK2";
        const int cap = 3;
        const int shoppers = 10;
        var storefrontId = await LiveStorefrontAsync(currency);
        var promotionId = await CouponAsync(storefrontId, currency, "RACE3", percentOff: 10, maxRedemptions: cap);
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

            // Distinct emails, so ONLY the total cap can refuse them (not the per-customer limit).
            var responses = await Task.WhenAll(clients.Select((c, i) =>
                c.PostAsJsonAsync("/checkout", CheckoutBody($"racer{i}@example.com", "RACE3"))));

            var accepted = responses.Count(r => r.IsSuccessStatusCode);
            var refused = responses.Where(r => r.StatusCode == HttpStatusCode.BadRequest).ToList();
            Assert.Equal(cap, accepted);
            Assert.Equal(shoppers - cap, refused.Count);
            foreach (var r in refused)
            {
                Assert.Contains("usage limit", await r.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            }

            // The counter agrees with the redemption rows — the cap was never exceeded, not even briefly.
            Assert.Equal(cap, await RedeemedCountAsync(promotionId));
            Assert.Equal(cap, await HeldRedemptionsAsync(promotionId));

            foreach (var r in responses.Where(r => r.IsSuccessStatusCode))
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
    public async Task The_per_customer_limit_counts_a_guest_by_checkout_email()
    {
        // A guest has no account, so the limit keys on the normalized checkout email — signing out (or
        // clearing the cart cookie, as a fresh client does) must not hand the shopper a second redemption.
        const string currency = "QK3";
        var storefrontId = await LiveStorefrontAsync(currency);
        await CouponAsync(storefrontId, currency, "ONEEACH", percentOff: 10, maxRedemptionsPerCustomer: 1);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var first = NewShopper(storefrontId);
        (await first.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var accepted = await first.PostAsJsonAsync("/checkout", CheckoutBody("repeat@example.com", "ONEEACH"));
        accepted.EnsureSuccessStatusCode();

        // A brand-new browser, the same person: same email in a different casing, with spaces.
        using var second = NewShopper(storefrontId);
        (await second.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var refused = await second.PostAsJsonAsync("/checkout", CheckoutBody("Repeat@Example.com", "ONEEACH"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("already used", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // A DIFFERENT customer is unaffected — the limit is per customer, not per coupon.
        using var third = NewShopper(storefrontId);
        (await third.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        (await third.PostAsJsonAsync("/checkout", CheckoutBody("someone.else@example.com", "ONEEACH")))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_failed_payment_releases_the_hold_so_the_code_can_be_used_again()
    {
        // A single-use coupon must not be burned by a checkout that never paid. PaymentFailed drives the
        // saga AwaitingPayment → Cancelled → OrderCancelled, which is the one path a failed payment, an
        // explicit cancel and the 30-minute expiry all funnel through.
        const string currency = "QK4";
        var storefrontId = await LiveStorefrontAsync(currency);
        var promotionId = await CouponAsync(storefrontId, currency, "SINGLE", percentOff: 10, maxRedemptions: 1);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var abandoning = NewShopper(storefrontId);
        (await abandoning.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var doomed = await CheckoutAsync(abandoning, coupon: "SINGLE");
        Assert.Equal(1, await RedeemedCountAsync(promotionId));

        // While the hold stands, the last redemption is genuinely gone.
        using var blocked = NewShopper(storefrontId);
        (await blocked.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await blocked.PostAsJsonAsync("/checkout", CheckoutBody("blocked@example.com", "SINGLE"))).StatusCode);

        await WaitForSagaAsync(doomed.OrderId);
        await fixture.PublishAsync(new PaymentFailed(doomed.OrderId, $"pi_fake_{doomed.OrderId:N}", "card declined"));
        await WaitForRedemptionStatusAsync(doomed.OrderId, PromotionRedemptionStatus.Released);
        await WaitForRedeemedCountAsync(promotionId, 0);

        // The code is spendable again — and by somebody else.
        using var lucky = NewShopper(storefrontId);
        (await lucky.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var reused = await CheckoutAsync(lucky, coupon: "SINGLE", email: "lucky@example.com");
        Assert.Equal(1_000, reused.DiscountMinor);
        Assert.Equal(1, await RedeemedCountAsync(promotionId));
    }

    [Fact]
    public async Task Confirm_and_release_are_idempotent_under_redelivery()
    {
        // Consumers must be safe to redeliver (messaging invariant). A second CheckoutCompleted must not
        // confirm twice, and a second OrderCancelled must not give the count back twice — the guard is the
        // status, so a released redemption stays released and the counter never goes negative.
        const string currency = "QK5";
        var storefrontId = await LiveStorefrontAsync(currency);
        var promotionId = await CouponAsync(storefrontId, currency, "IDEMP", percentOff: 10, maxRedemptions: 5);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = NewShopper(storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper, coupon: "IDEMP");

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
        await WaitForRedemptionStatusAsync(order.OrderId, PromotionRedemptionStatus.Confirmed);
        var confirmedAt = await ConfirmedAtAsync(order.OrderId);

        // Redeliver both terminal messages.
        await fixture.PublishAsync(new CheckoutCompleted(order.OrderId));
        await fixture.PublishAsync(new OrderCancelled(order.OrderId, "duplicate delivery"));
        await Task.Delay(1_500);

        Assert.Equal(PromotionRedemptionStatus.Confirmed, await RedemptionStatusAsync(order.OrderId));
        Assert.Equal(confirmedAt, await ConfirmedAtAsync(order.OrderId)); // not re-stamped
        Assert.Equal(1, await RedeemedCountAsync(promotionId)); // never double-counted, never given back
        Assert.Equal(1, await HeldRedemptionsAsync(promotionId));
    }

    [Fact]
    public async Task A_reservation_whose_checkout_attempt_never_committed_is_swept_and_the_cap_recovers()
    {
        // The one crash window the design leaves: the reservation commits in its own transaction, so a
        // process death before the checkout attempt commits would strand a hold and burn a limited code
        // for nobody. The second-chance sweep reclaims exactly that — a RESERVED row past the saga's
        // expiry with NO checkout attempt and NO order — and only when a claim would otherwise be refused.
        const string currency = "QK8";
        var storefrontId = await LiveStorefrontAsync(currency);
        var promotionId = await CouponAsync(storefrontId, currency, "STALE", percentOff: 10, maxRedemptions: 1);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        // Simulate the crash: a hold that was taken two hours ago for an order that never materialized.
        var orphanOrderId = Guid.CreateVersion7();
        using (var scope = fixture.Ordering.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            db.PromotionRedemptions.Add(new PromotionRedemption
            {
                Id = Guid.CreateVersion7(),
                PromotionId = promotionId,
                TenantId = TenantId,
                OrderId = orphanOrderId,
                CustomerKey = "e:ghost@example.com",
                Code = "STALE",
                Status = PromotionRedemptionStatus.Reserved,
                ReservedAt = DateTimeOffset.UtcNow.AddHours(-2),
            });
            var copy = await db.PromotionCopies.FirstAsync(p => p.PromotionId == promotionId);
            copy.RedeemedCount = 1; // the counter the lost checkout incremented
            await db.SaveChangesAsync();
        }

        // The cap now reads as exhausted, so this checkout is exactly the case that triggers the sweep.
        using var shopper = NewShopper(storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper, coupon: "STALE", email: "real.shopper@example.com");

        Assert.Equal(1_000, order.DiscountMinor);
        Assert.Equal("STALE", order.CouponCode);

        // The orphan is released and the single allowance now belongs to the real order — not both.
        using (var scope = fixture.Ordering.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var orphan = await db.PromotionRedemptions.AsNoTracking().FirstAsync(r => r.OrderId == orphanOrderId);
            Assert.Equal(PromotionRedemptionStatus.Released, orphan.Status);
        }

        Assert.Equal(1, await RedeemedCountAsync(promotionId));
        Assert.Equal(1, await HeldRedemptionsAsync(promotionId));
        Assert.Equal(PromotionRedemptionStatus.Reserved, await RedemptionStatusAsync(order.OrderId));
    }

    [Fact]
    public async Task Every_refusal_reports_its_own_reason_on_the_cart_preview_and_at_checkout()
    {
        // The shopper must be able to act on the answer, so a bad code is never a blanket "invalid".
        // 2 = UnknownCode, 5 = Expired, 7 = ThresholdNotMet, 1 = Applied (Ordering's CouponStatus).
        const string currency = "QK6";
        var storefrontId = await LiveStorefrontAsync(currency);
        await CouponAsync(storefrontId, currency, "GOOD", percentOff: 10);
        await CouponAsync(
            storefrontId, currency, "OLD", percentOff: 10,
            activeFrom: DateTimeOffset.UtcNow.AddDays(-10), activeUntil: DateTimeOffset.UtcNow.AddDays(-1));
        await CouponAsync(storefrontId, currency, "BIGSPEND", percentOff: 10, minimumAmountMinor: 500_000);

        var productId = await fixture.SeedProductAsync(10_000, currency);
        using var shopper = NewShopper(storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();

        Assert.Equal(1, (await SummaryAsync(shopper, storefrontId, "GOOD")).CouponStatus);
        Assert.Equal(2, (await SummaryAsync(shopper, storefrontId, "NOSUCHCODE")).CouponStatus);
        Assert.Equal(5, (await SummaryAsync(shopper, storefrontId, "OLD")).CouponStatus);
        Assert.Equal(7, (await SummaryAsync(shopper, storefrontId, "BIGSPEND")).CouponStatus);

        // The preview and the charge agree: a valid code discounts the preview…
        var good = await SummaryAsync(shopper, storefrontId, "GOOD");
        Assert.Equal(1_000, good.PromotionDiscountMinor);
        Assert.Equal(9_000, good.ItemsTotalMinor);
        // …and an invalid one leaves it at full price rather than quietly pretending.
        Assert.Equal(0, (await SummaryAsync(shopper, storefrontId, "OLD")).PromotionDiscountMinor);

        // Checkout refuses the same codes, each naming its own reason.
        foreach (var (code, fragment) in new[] { ("NOSUCHCODE", "not recognised"), ("OLD", "expired"), ("BIGSPEND", "conditions") })
        {
            var refused = await shopper.PostAsJsonAsync("/checkout", CheckoutBody("reasons@example.com", code));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains(fragment, await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_coupon_for_another_storefront_is_refused_rather_than_silently_ignored()
    {
        // A real code aimed at a different store must say so — "unknown code" would send the shopper
        // hunting for a typo that isn't there.
        const string currency = "QK7";
        var mine = await LiveStorefrontAsync(currency);
        var theirs = await LiveStorefrontAsync(currency);
        await CouponAsync(theirs, currency, "THEIRS", percentOff: 10);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = NewShopper(mine);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();

        Assert.Equal(6, (await SummaryAsync(shopper, mine, "THEIRS")).CouponStatus); // WrongStorefront
        var refused = await shopper.PostAsJsonAsync("/checkout", CheckoutBody("wrong@example.com", "THEIRS"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("this store", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
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

    private static async Task<CartSummaryDto> SummaryAsync(HttpClient client, Guid storefrontId, string coupon) =>
        (await client.GetFromJsonAsync<CartSummaryDto>(
            $"/cart/summary?storefrontId={storefrontId}&couponCode={Uri.EscapeDataString(coupon)}"))!;

    /// <summary>A live storefront in its own currency, so no other test's cart can pick up its promotions.</summary>
    private async Task<Guid> LiveStorefrontAsync(string currency)
    {
        var storefrontId = Guid.CreateVersion7();
        await fixture.PublishAsync(new StorefrontConfigChanged(
            storefrontId, TenantId, $"Coupon Store {currency}", currency, 0, IsLive: true, TaxInclusive: false, DiscountBps: 0));
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            if (await db.StorefrontTaxCopies.AsNoTracking().AnyAsync(c => c.StorefrontId == storefrontId && c.IsLive))
            {
                return storefrontId;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Storefront {storefrontId} did not project.");
    }

    /// <summary>Publishes a CODE-GATED promotion as Catalog would, and waits for Ordering's copy.</summary>
    private async Task<Guid> CouponAsync(
        Guid storefrontId, string currency, string code, int percentOff,
        long minimumAmountMinor = 0, int? maxRedemptions = null, int? maxRedemptionsPerCustomer = null,
        DateTimeOffset? activeFrom = null, DateTimeOffset? activeUntil = null)
    {
        var promotionId = Guid.CreateVersion7();
        await fixture.PublishAsync(new PromotionChanged(
            promotionId, TenantId, storefrontId, $"Coupon {code}", currency,
            PromotionScopeKind.Storefront, ProductId: null,
            MinimumAmountMinor: minimumAmountMinor, MinimumQuantity: 0,
            GrantsFreeShipping: false, PercentOff: percentOff, DiscountAmountMinor: 0,
            Combinable: false, Active: true, ActiveFrom: activeFrom, ActiveUntil: activeUntil,
            Code: code, MaxRedemptions: maxRedemptions, MaxRedemptionsPerCustomer: maxRedemptionsPerCustomer));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var copy = await db.PromotionCopies.AsNoTracking().FirstOrDefaultAsync(p => p.PromotionId == promotionId);
            if (copy?.Code == code)
            {
                return promotionId;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Coupon promotion {promotionId} ({code}) did not project.");
    }

    private async Task<int> RedeemedCountAsync(Guid promotionId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return (await db.PromotionCopies.AsNoTracking().FirstAsync(p => p.PromotionId == promotionId)).RedeemedCount;
    }

    private async Task<int> HeldRedemptionsAsync(Guid promotionId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return await db.PromotionRedemptions.AsNoTracking()
            .CountAsync(r => r.PromotionId == promotionId && r.Status != PromotionRedemptionStatus.Released);
    }

    private async Task<PromotionRedemptionStatus?> RedemptionStatusAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return (await db.PromotionRedemptions.AsNoTracking().FirstOrDefaultAsync(r => r.OrderId == orderId))?.Status;
    }

    private async Task<DateTimeOffset?> ConfirmedAtAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return (await db.PromotionRedemptions.AsNoTracking().FirstAsync(r => r.OrderId == orderId)).ConfirmedAt;
    }

    private async Task WaitForRedemptionStatusAsync(Guid orderId, PromotionRedemptionStatus expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await RedemptionStatusAsync(orderId) == expected)
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Redemption for order {orderId} did not reach {expected}.");
    }

    private async Task WaitForRedeemedCountAsync(Guid promotionId, int expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await RedeemedCountAsync(promotionId) == expected)
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Promotion {promotionId} RedeemedCount did not reach {expected}.");
    }

    private async Task SimulatePaymentAsync(Guid orderId, long gross)
    {
        await WaitForSagaAsync(orderId);
        using var payments = fixture.Payments.CreateClient();
        var response = await payments.PostAsync($"/dev/simulate-payment/pi_fake_{orderId:N}?amountMinor={gross}", null);
        response.EnsureSuccessStatusCode();
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

            await Task.Delay(300);
        }

        throw new TimeoutException($"Order {orderId} did not reach {expected}.");
    }
}
