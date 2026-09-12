using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// What a subscription RENEWAL is charged when the first period was discounted (ADR-0057). The promotion
/// carries the answer: <c>AppliesToRenewals = false</c> is an introductory offer (the first period is
/// discounted, renewals go back to list) and <c>true</c> keeps the discount for the life of the
/// subscription.
/// <para>
/// The storefront-wide discount (ADR-0053) is a point-of-sale setting rather than a term of the
/// subscription, so it discounts the first period and never rides a renewal — pinned here too, because it
/// is the half of the rule nobody would think to check.
/// </para>
/// <para>
/// Before this, Ordering published the line's UNDISCOUNTED price as the subscription price unconditionally
/// — the introductory behaviour, reached by accident rather than by decision, with no way to ask for
/// anything else.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class SubscriptionRenewalPriceTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private sealed record CheckoutResponseDto(
        Guid OrderId, string? ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
        long GrossMinor, string Currency, string? Message);

    [Fact]
    public async Task An_introductory_promotion_discounts_the_first_period_and_renewals_charge_list()
    {
        const string currency = "QS1";
        var storefrontId = await LiveStorefrontAsync(currency, discountBps: 0);
        var productId = await fixture.SeedRecurringProductAsync(2_000, currency);
        await PromotionAsync(storefrontId, currency, productId, percentOff: 25, appliesToRenewals: false);

        var order = await SubscribeAsync(storefrontId, productId, "intro@example.com");

        // The shopper paid the discounted price for the period they bought.
        Assert.Equal(500, order.DiscountMinor);
        Assert.Equal(0, await RenewalDiscountOfAsync(order.OrderId));

        // The subscription that will be charged from now on is at LIST.
        Assert.Equal(2_000, await SubscriptionPriceAsync(order.OrderId));
    }

    [Fact]
    public async Task A_promotion_flagged_for_renewals_keeps_its_price_for_the_life_of_the_subscription()
    {
        const string currency = "QS2";
        var storefrontId = await LiveStorefrontAsync(currency, discountBps: 0);
        var productId = await fixture.SeedRecurringProductAsync(2_000, currency);
        await PromotionAsync(storefrontId, currency, productId, percentOff: 25, appliesToRenewals: true);

        var order = await SubscribeAsync(storefrontId, productId, "forever@example.com");

        Assert.Equal(500, order.DiscountMinor);
        Assert.Equal(500, await RenewalDiscountOfAsync(order.OrderId));
        Assert.Equal(1_500, await SubscriptionPriceAsync(order.OrderId));
    }

    [Fact]
    public async Task The_storefront_wide_discount_never_rides_a_renewal()
    {
        // A store-wide sale is a point-of-sale setting, not a subscription term: it must not lock a
        // transient 10% into a subscription forever. The permanent promotion still carries.
        const string currency = "QS3";
        var storefrontId = await LiveStorefrontAsync(currency, discountBps: 1_000); // 10% store-wide
        var productId = await fixture.SeedRecurringProductAsync(2_000, currency);
        await PromotionAsync(storefrontId, currency, productId, percentOff: 25, appliesToRenewals: true);

        var order = await SubscribeAsync(storefrontId, productId, "storewide@example.com");

        // First period: 25% promotion (500) + 10% store-wide on the 2000 subtotal (200).
        Assert.Equal(700, order.DiscountMinor);

        // The renewal keeps only the promotion's 500 — never the store-wide 200.
        Assert.Equal(500, await RenewalDiscountOfAsync(order.OrderId));
        Assert.Equal(1_500, await SubscriptionPriceAsync(order.OrderId));
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    /// <summary>A verified member with a stored instrument — the only shopper allowed to buy a subscription.</summary>
    private async Task<CheckoutResponseDto> SubscribeAsync(Guid storefrontId, Guid productId, string email)
    {
        var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
        client.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName,
            fixture.MintInternalClaims(Guid.NewGuid(), "customer", email, emailVerified: true));

        (await client.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var response = await client.PostAsJsonAsync("/checkout", new
        {
            email,
            shippingAddress = new { name = "M", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
            savedPaymentMethodId = Guid.NewGuid(),
        });
        response.EnsureSuccessStatusCode();
        var order = (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        // The saga must exist BEFORE the payment lands, or the event arrives with nothing to receive it and
        // the order is never confirmed (the same wait every other checkout test does).
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.CheckoutStates.AsNoTracking().AnyAsync(x => x.CorrelationId == order.OrderId);
        }, $"Checkout saga for {order.OrderId} did not start");

        using var payments = fixture.Payments.CreateClient();
        (await payments.PostAsync(
            $"/dev/simulate-payment/pi_fake_{order.OrderId:N}?amountMinor={order.GrossMinor}", null))
            .EnsureSuccessStatusCode();

        client.Dispose();
        return order;
    }

    /// <summary>The renewal-carrying slice checkout persisted on the order's single line.</summary>
    private async Task<long> RenewalDiscountOfAsync(Guid orderId)
    {
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.Orders.AsNoTracking().AnyAsync(o => o.Id == orderId);
        }, $"Order {orderId} was never created");

        using var read = fixture.Ordering.Services.CreateScope();
        var orders = read.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var order = await orders.Orders.AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.Id == orderId);
        return order.Lines.Single().RenewalDiscountMinor;
    }

    /// <summary>The price Payments will actually charge on every renewal from here on.</summary>
    private async Task<long> SubscriptionPriceAsync(Guid orderId)
    {
        long price = -1;
        await WaitAsync(async () =>
        {
            using var scope = fixture.Payments.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var subscription = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.OrderId == orderId);
            if (subscription is null)
            {
                return false;
            }

            price = subscription.PriceMinor;
            return true;
        }, $"Subscription for order {orderId} was never set up");
        return price;
    }

    private async Task<Guid> LiveStorefrontAsync(string currency, int discountBps)
    {
        var storefrontId = Guid.CreateVersion7();
        await fixture.PublishAsync(new StorefrontConfigChanged(
            storefrontId, TenantId, $"Renewal Store {currency}", currency, 0,
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

    private async Task<Guid> PromotionAsync(
        Guid storefrontId, string currency, Guid productId, int percentOff, bool appliesToRenewals)
    {
        var promotionId = Guid.CreateVersion7();
        await fixture.PublishAsync(new PromotionChanged(
            promotionId, TenantId, storefrontId, "Subscription promo", currency,
            PromotionScopeKind.Product, ProductId: productId,
            MinimumAmountMinor: 0, MinimumQuantity: 0,
            GrantsFreeShipping: false, PercentOff: percentOff, DiscountAmountMinor: 0,
            Combinable: true, Active: true, ActiveFrom: null, ActiveUntil: null,
            Code: null, MaxRedemptions: null, MaxRedemptionsPerCustomer: null,
            AppliesToRenewals: appliesToRenewals));
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var copy = await db.PromotionCopies.AsNoTracking().FirstOrDefaultAsync(p => p.PromotionId == promotionId);
            return copy is not null && copy.AppliesToRenewals == appliesToRenewals;
        }, $"Promotion {promotionId} did not project");
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
