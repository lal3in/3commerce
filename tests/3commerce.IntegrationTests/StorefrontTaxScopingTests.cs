using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Tax belongs to a STOREFRONT, not to a currency (rev_tax).
/// <para>
/// Checkout used to resolve the rate — and the far more dangerous <c>TaxInclusive</c> flag — with
/// <c>Where(t =&gt; t.IsLive &amp;&amp; t.Currency == currency).OrderByDescending(TaxRateBasisPoints)</c>,
/// scoped to neither storefront nor tenant. Two unrelated live EUR stores were therefore one pool: the
/// 0% store charged the 25% store's rate, in the 25% store's regime. This suite is the proof it can't
/// happen again, and it is exactly the scenario the rest of the pricing suites had to invent a unique
/// fake three-letter currency per test to dodge.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class StorefrontTaxScopingTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenantId = new("00000000-0000-0000-0000-0000000000f1");

    private sealed record CheckoutResponseDto(
        Guid OrderId, string ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
        long GrossMinor, string Currency, string? Message, bool FreeShippingApplied = false,
        List<Guid>? AppliedPromotionIds = null, string? CouponCode = null);

    private sealed record StatusDto(Guid Id, string Status);

    [Fact]
    public async Task Two_live_storefronts_in_one_currency_each_charge_their_own_rate_and_regime()
    {
        // The SAME currency, two live stores, two very different tax stories:
        //   ZERO — 0 bps, exclusive  → no tax at all
        //   HIGH — 2500 bps, INCLUSIVE (an EU-VAT-shaped store) → the shelf price already contains it
        // Under the old by-currency lookup the zero-rate store picked up 2500 bps AND inclusiveness,
        // charging 2100 of tax it should never have collected.
        const string currency = "QTX";
        var zero = await SeedStorefrontAsync(currency, "Zero Tax Store", taxBps: 0, inclusive: false);
        var high = await SeedStorefrontAsync(currency, "High Tax Store", taxBps: 2_500, inclusive: true);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        // The 0% store: goods + shipping, no tax.
        using var zeroShopper = Shopper(zero);
        (await zeroShopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var zeroOrder = await CheckoutAsync(zeroShopper);
        Assert.Equal(0, zeroOrder.TaxMinor);
        Assert.Equal(10_000, zeroOrder.NetMinor);
        Assert.Equal(499, zeroOrder.ShippingMinor);
        Assert.Equal(10_499, zeroOrder.GrossMinor);
        AssertMoneyIdentity(zeroOrder);

        // The 25% INCLUSIVE store, same cart: the shopper still pays the listed 10499, and the tax is
        // the portion contained in it — 10499 × 2500 / 12500 = 2099.8 → 2100.
        using var highShopper = Shopper(high);
        (await highShopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var highOrder = await CheckoutAsync(highShopper);
        Assert.Equal(2_100, highOrder.TaxMinor);
        Assert.Equal(10_499, highOrder.GrossMinor); // inclusive: tax is INSIDE the charge, not added

        // Both settle, and the books still balance.
        await SettleAsync(zeroShopper, zeroOrder);
        await SettleAsync(highShopper, highOrder);
    }

    [Fact]
    public async Task An_exclusive_store_adds_its_own_rate_while_a_neighbour_at_a_higher_rate_does_not_bleed()
    {
        // The exclusive half of the same story, and the one the old OrderByDescending made worst: the
        // HIGHEST live rate in the currency always won, so the quiet store paid the loud store's tax.
        const string currency = "QTY";
        var low = await SeedStorefrontAsync(currency, "Low Tax Store", taxBps: 500, inclusive: false);
        await SeedStorefrontAsync(currency, "Loud Tax Store", taxBps: 3_000, inclusive: false);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = Shopper(low);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper);

        // 5% on goods + shipping: (10000 + 499) × 0.05 = 524.95 → 525.
        Assert.Equal(525, order.TaxMinor);
        Assert.Equal(11_024, order.GrossMinor);
        AssertMoneyIdentity(order);
        await SettleAsync(shopper, order);
    }

    [Fact]
    public async Task Another_tenants_live_storefront_in_the_same_currency_is_not_a_tax_source()
    {
        // Tenant isolation, the other half of the missing scope: a completely unrelated business's live
        // store sharing a currency must not price this one's checkout.
        const string currency = "QTZ";
        var mine = await SeedStorefrontAsync(currency, "My Store", taxBps: 0, inclusive: false);
        await SeedStorefrontAsync(currency, "Someone Else's Store", taxBps: 2_000, inclusive: false, tenantId: OtherTenantId);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = Shopper(mine);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper);

        Assert.Equal(0, order.TaxMinor);
        Assert.Equal(10_499, order.GrossMinor);
        await SettleAsync(shopper, order);
    }

    [Fact]
    public async Task A_storefront_that_is_not_live_cannot_be_checked_out_at_all()
    {
        // The explicit decision behind the scoping (documented in ADR-0055): the old query filtered
        // IsLive, so a Draft/Paused/Archived store never contributed a rate. Now that the rate comes
        // from THIS store's copy, "not live" can no longer mean "sell it untaxed" — it means don't sell.
        const string currency = "QTW";
        var storefrontId = await SeedStorefrontAsync(currency, "Paused Store", taxBps: 1_000, inclusive: false);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        using var shopper = Shopper(storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();

        // Live: sells, at its own 10%.
        var live = await CheckoutAsync(shopper);
        Assert.Equal(1_050, live.TaxMinor); // (10000 + 499) × 0.10 = 1049.9 → 1050
        await SettleAsync(shopper, live);

        // Paused: refused before any payment intent exists.
        await PauseAsync(storefrontId, currency, taxBps: 1_000);
        using var later = Shopper(storefrontId);
        (await later.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var refused = await later.PostAsJsonAsync("/checkout", CheckoutBody());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("not currently open for orders", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// NetMinor is the PRE-discount subtotal, so the shopper pays <c>Net − Discount + Ship + Tax</c>.
    /// (Only meaningful on an exclusive regime; an inclusive charge carries its tax inside the gross.)
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

    private static object CheckoutBody() => new
    {
        email = "buyer@example.com",
        shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
    };

    private static async Task<CheckoutResponseDto> CheckoutAsync(HttpClient shopper)
    {
        var response = await shopper.PostAsJsonAsync("/checkout", CheckoutBody());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
    }

    private async Task SettleAsync(HttpClient shopper, CheckoutResponseDto order)
    {
        await WaitForSagaAsync(order.OrderId);
        using var payments = fixture.Payments.CreateClient();
        (await payments.PostAsync($"/dev/simulate-payment/pi_fake_{order.OrderId:N}?amountMinor={order.GrossMinor}", null))
            .EnsureSuccessStatusCode();
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    private async Task<Guid> SeedStorefrontAsync(string currency, string name, int taxBps, bool inclusive, Guid? tenantId = null)
    {
        var storefrontId = Guid.CreateVersion7();
        await fixture.PublishAsync(new StorefrontConfigChanged(
            storefrontId, tenantId ?? TenantId, name, currency, taxBps, IsLive: true, TaxInclusive: inclusive, DiscountBps: 0));
        await WaitForCopyAsync(storefrontId, c => c.IsLive && c.TaxRateBasisPoints == taxBps && c.TaxInclusive == inclusive);
        return storefrontId;
    }

    private async Task PauseAsync(Guid storefrontId, string currency, int taxBps)
    {
        await fixture.PublishAsync(new StorefrontConfigChanged(
            storefrontId, TenantId, "Paused Store", currency, taxBps, IsLive: false, TaxInclusive: false, DiscountBps: 0));
        await WaitForCopyAsync(storefrontId, c => !c.IsLive);
    }

    private async Task WaitForCopyAsync(Guid storefrontId, Func<ThreeCommerce.Ordering.Domain.StorefrontTaxCopy, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var copy = await db.StorefrontTaxCopies.AsNoTracking().FirstOrDefaultAsync(c => c.StorefrontId == storefrontId);
            if (copy is not null && predicate(copy))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Storefront copy {storefrontId} did not reach the expected shape.");
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

        throw new TimeoutException($"Checkout saga for {orderId} never started.");
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
}
