using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// What a REFUND does to a coupon redemption (ADR-0056): nothing. A coupon is spent when the sale is
/// made, and reversing the money does not un-spend it — the allowance rations the discount, not the
/// revenue.
/// <para>
/// This was always the behaviour (<c>ReleaseAsync</c> is status-guarded to Reserved rows, and no refund
/// or dispute consumer calls it), but nothing recorded it as a DECISION and nothing tested it, so it was
/// indistinguishable from an oversight. These cases make it verifiable: a future refactor that
/// "helpfully" releases on refund fails here instead of quietly opening a refund-shaped hole in every
/// limited code — buy with the last redemption, refund, use it again, repeat.
/// </para>
/// <para>
/// The contrast — a checkout that never confirms DOES release — is pinned by
/// <see cref="CouponRedemptionTests"/>; the two together cover both halves of the rule, and neither can
/// drift without the other noticing.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class CouponRefundPolicyTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private sealed record CheckoutResponseDto(
        Guid OrderId, string ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
        long GrossMinor, string Currency, string? Message, bool FreeShippingApplied = false,
        List<Guid>? AppliedPromotionIds = null, string? CouponCode = null);

    private sealed record StatusDto(Guid Id, string Status);

    [Fact]
    public async Task A_fully_refunded_order_keeps_its_redemption_spent()
    {
        // THE case. A single-use code, spent on a sale that is then refunded in full. The money goes
        // back; the allowance does not. Releasing here would let any shopper reset a one-shot code on
        // demand by returning the order — rationing a customer can undo is not rationing.
        const string currency = "QR1";
        const string email = "returner@example.com";
        var storefrontId = await LiveStorefrontAsync(currency);
        var promotionId = await CouponAsync(storefrontId, currency, "ONESHOT", maxRedemptions: 1);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        var order = await ConfirmedOrderAsync(storefrontId, productId, "ONESHOT", email);
        Assert.Equal(1_000, order.DiscountMinor);
        Assert.Equal(1, await RedeemedCountAsync(promotionId));

        await fixture.PublishAsync(new RefundCompleted(
            Guid.CreateVersion7(), order.OrderId, order.GrossMinor, FullyRefunded: true));
        await WaitForOrderStatusAsync(storefrontId, order.OrderId, "Refunded");

        // The redemption is untouched, and the code is still gone.
        Assert.Equal(PromotionRedemptionStatus.Confirmed, await RedemptionStatusAsync(order.OrderId));
        Assert.Equal(1, await RedeemedCountAsync(promotionId));
        Assert.Equal(HttpStatusCode.BadRequest, await RetryCodeAsync(storefrontId, productId, "ONESHOT", email));
    }

    [Fact]
    public async Task A_chargeback_keeps_its_redemption_spent()
    {
        // The adversarial version of the same case: the shopper may be disputing in bad faith, so handing
        // the allowance back is the worst reading of the refund hole, not a kindness.
        const string currency = "QR2";
        const string email = "disputer@example.com";
        var storefrontId = await LiveStorefrontAsync(currency);
        var promotionId = await CouponAsync(storefrontId, currency, "DISPUTED", maxRedemptions: 1);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        var order = await ConfirmedOrderAsync(storefrontId, productId, "DISPUTED", email);
        Assert.Equal(1, await RedeemedCountAsync(promotionId));

        await fixture.PublishAsync(new PaymentChargedBack(
            order.OrderId, $"pi_fake_{order.OrderId:N}", order.GrossMinor));

        // Nothing to wait for on the redemption — the assertion is that it never moves — so settle the
        // consumer by waiting for the badge the chargeback DOES set (Disputed: a lost dispute IS the
        // terminal chargeback outcome, and the consumer sets it even if PaymentDisputed was never seen).
        await WaitForChargebackAsync(order.OrderId);
        Assert.Equal(PromotionRedemptionStatus.Confirmed, await RedemptionStatusAsync(order.OrderId));
        Assert.Equal(1, await RedeemedCountAsync(promotionId));
        Assert.Equal(HttpStatusCode.BadRequest, await RetryCodeAsync(storefrontId, productId, "DISPUTED", email));
    }

    [Fact]
    public async Task A_partial_refund_keeps_both_the_order_and_its_redemption()
    {
        // A partial refund does not even end the sale — the order stays Confirmed — so there is certainly
        // no argument for giving the coupon back.
        const string currency = "QR3";
        const string email = "partial@example.com";
        var storefrontId = await LiveStorefrontAsync(currency);
        var promotionId = await CouponAsync(storefrontId, currency, "PARTIAL", maxRedemptions: 1);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        var order = await ConfirmedOrderAsync(storefrontId, productId, "PARTIAL", email);

        await fixture.PublishAsync(new RefundCompleted(
            Guid.CreateVersion7(), order.OrderId, 500, FullyRefunded: false));
        await WaitForPartialRefundAsync(order.OrderId);

        Assert.Equal("Confirmed", await OrderStatusAsync(storefrontId, order.OrderId));
        Assert.Equal(PromotionRedemptionStatus.Confirmed, await RedemptionStatusAsync(order.OrderId));
        Assert.Equal(1, await RedeemedCountAsync(promotionId));
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    /// <summary>Checks out with the code, pays, and waits for the order AND the redemption to settle.</summary>
    private async Task<CheckoutResponseDto> ConfirmedOrderAsync(
        Guid storefrontId, Guid productId, string code, string email)
    {
        using var shopper = NewShopper(storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var response = await shopper.PostAsJsonAsync("/checkout", CheckoutBody(email, code));
        response.EnsureSuccessStatusCode();
        var order = (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        Assert.Equal(code, order.CouponCode);

        await WaitForSagaAsync(order.OrderId);
        using var payments = fixture.Payments.CreateClient();
        (await payments.PostAsync(
            $"/dev/simulate-payment/pi_fake_{order.OrderId:N}?amountMinor={order.GrossMinor}", null))
            .EnsureSuccessStatusCode();

        await WaitForOrderStatusAsync(storefrontId, order.OrderId, "Confirmed");
        await WaitForRedemptionStatusAsync(order.OrderId, PromotionRedemptionStatus.Confirmed);
        return order;
    }

    /// <summary>The same shopper trying the same code again — the refusal the policy exists to produce.</summary>
    private async Task<HttpStatusCode> RetryCodeAsync(Guid storefrontId, Guid productId, string code, string email)
    {
        using var again = NewShopper(storefrontId);
        (await again.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        return (await again.PostAsJsonAsync("/checkout", CheckoutBody(email, code))).StatusCode;
    }

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

    private async Task<string?> OrderStatusAsync(Guid storefrontId, Guid orderId)
    {
        using var client = NewShopper(storefrontId);
        return (await client.GetFromJsonAsync<StatusDto>($"/orders/{orderId}/status"))?.Status;
    }

    private async Task WaitForOrderStatusAsync(Guid storefrontId, Guid orderId, string expected) =>
        await WaitAsync(async () => await OrderStatusAsync(storefrontId, orderId) == expected,
            $"Order {orderId} did not reach {expected}");

    private async Task WaitForChargebackAsync(Guid orderId) =>
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.Orders.AsNoTracking().AnyAsync(o => o.Id == orderId && o.Disputed);
        }, $"Order {orderId} was never marked disputed");

    private async Task WaitForPartialRefundAsync(Guid orderId) =>
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.Orders.AsNoTracking().AnyAsync(o => o.Id == orderId && o.PartiallyRefunded);
        }, $"Order {orderId} was never marked partially refunded");

    private async Task WaitForRedemptionStatusAsync(Guid orderId, PromotionRedemptionStatus expected) =>
        await WaitAsync(async () => await RedemptionStatusAsync(orderId) == expected,
            $"Redemption for {orderId} did not reach {expected}");

    private async Task WaitForSagaAsync(Guid orderId) =>
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.CheckoutStates.AsNoTracking().AnyAsync(s => s.CorrelationId == orderId);
        }, $"Checkout saga for {orderId} did not start");

    private async Task<PromotionRedemptionStatus?> RedemptionStatusAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return (await db.PromotionRedemptions.AsNoTracking().FirstOrDefaultAsync(r => r.OrderId == orderId))?.Status;
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
            storefrontId, TenantId, $"Refund Policy Store {currency}", currency, 0,
            IsLive: true, TaxInclusive: false, DiscountBps: 0));
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.StorefrontTaxCopies.AsNoTracking().AnyAsync(c => c.StorefrontId == storefrontId && c.IsLive);
        }, $"Storefront {storefrontId} did not project");
        return storefrontId;
    }

    private async Task<Guid> CouponAsync(Guid storefrontId, string currency, string code, int? maxRedemptions)
    {
        var promotionId = Guid.CreateVersion7();
        await fixture.PublishAsync(new PromotionChanged(
            promotionId, TenantId, storefrontId, $"Coupon {code}", currency,
            PromotionScopeKind.Storefront, ProductId: null,
            MinimumAmountMinor: 0, MinimumQuantity: 0,
            GrantsFreeShipping: false, PercentOff: 10, DiscountAmountMinor: 0,
            Combinable: false, Active: true, ActiveFrom: null, ActiveUntil: null,
            Code: code, MaxRedemptions: maxRedemptions, MaxRedemptionsPerCustomer: 1));
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
