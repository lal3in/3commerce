using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// renewal_usage_tax_2026_10_11 (ADR-0063 P4): subscription renewals and metered usage charges post the tax a
/// checkout of the same amount in the same storefront would have posted. Before this both posted tax 0, so on an
/// inclusive store (AU GST / EU VAT) the whole charge landed in <c>revenue.store-{id}</c> and output tax was
/// under-reported. Pinned end to end: the subscription snapshots its tax TERMS at purchase (Ordering →
/// <see cref="SubscriptionRequested"/>), Payments projects the storefront's RATE from
/// <see cref="StorefrontConfigChanged"/>, and the renewal entry splits net + tax with the gross unchanged.
/// Exclusive regimes are pinned at today's behaviour (no tax added, none posted) pending the owner's decision.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class RenewalUsageTaxTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private sealed record CheckoutResponseDto(
        Guid OrderId, string? ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
        long GrossMinor, string Currency, string? Message);

    private static string Rev(Guid s) => $"revenue.store-{s:N}";
    private static string Tax(Guid s) => $"tax.store-{s:N}";

    [Fact]
    public async Task An_AU_GST_renewal_posts_net_revenue_and_GST_to_the_stores_accounts_with_the_gross_unchanged()
    {
        // The full path: checkout on an inclusive 10% store → OrderStatusConsumer snapshots the terms →
        // Payments' subscription → renewal posts exactly the GST the first period's checkout extracted.
        const string currency = "RTA";
        var store = await StoreAsync(currency, bps: 1000, inclusive: true);
        var productId = await fixture.SeedRecurringProductAsync(2_200, currency);

        var order = await SubscribeAsync(store, productId, "gst-renew@example.com");
        Assert.Equal(2_200, order.GrossMinor); // inclusive: the shelf price IS the charge
        Assert.Equal(200, order.TaxMinor);     // checkout extracted A$2.00 GST

        var subscription = await SubscriptionOfAsync(order.OrderId);
        Assert.True(subscription.TaxInclusive); // terms snapshotted at purchase
        Assert.False(subscription.TaxExempt);

        var lines = await RenewAndReadAsync(subscription);

        Assert.Equal(2_000, Credit(lines, Rev(store)));   // net revenue (was 2200: the defect)
        Assert.Equal(200, Credit(lines, Tax(store)));     // GST == what checkout extracted on the same amount
        Assert.Equal(2_200, CashDebits(lines));           // the customer was charged the same gross
        AssertWellFormed(lines, currency);
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task An_EU_VAT_renewal_extracts_VAT_the_same_way()
    {
        const string currency = "RTE";
        var store = await StoreAsync(currency, bps: 2100, inclusive: true); // NL 21%
        var subscription = await StartAsync(store, currency, priceMinor: 1_999, taxInclusive: true);

        var lines = await RenewAndReadAsync(subscription);

        Assert.Equal(347, Credit(lines, Tax(store)));     // 1999 × 2100 / 12100 = 346.94 → 347
        Assert.Equal(1_652, Credit(lines, Rev(store)));
        Assert.Equal(1_999, CashDebits(lines));
        AssertWellFormed(lines, currency);
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task A_renewal_uses_the_rate_in_force_at_the_charge_not_the_rate_at_purchase()
    {
        // Rates change by law and each period is a new supply: the subscription snapshots its TERMS, not its rate.
        const string currency = "RTR";
        var store = await StoreAsync(currency, bps: 1000, inclusive: true);
        var subscription = await StartAsync(store, currency, priceMinor: 2_200, taxInclusive: true);
        await StoreAsync(currency, bps: 1500, inclusive: true, storefrontId: store); // the rate rises to 15%

        var lines = await RenewAndReadAsync(subscription);

        Assert.Equal(287, Credit(lines, Tax(store)));     // 2200 × 1500 / 11500 = 286.96 → 287
        Assert.Equal(1_913, Credit(lines, Rev(store)));
        Assert.Equal(2_200, CashDebits(lines));
    }

    [Fact]
    public async Task A_destination_exempt_subscription_renews_without_tax()
    {
        // The ship rule that exempted the line at checkout (ProductShipRule, ChargeDestinationTax = false for the
        // ship-to) is snapshotted, so its renewals stay exempt — never a tax the first period did not carry.
        const string currency = "RTX";
        var store = await StoreAsync(currency, bps: 1000, inclusive: true);
        var productId = await fixture.SeedRecurringProductAsync(2_200, currency);
        await ExemptForGermanyAsync(productId);

        var order = await SubscribeAsync(store, productId, "exempt-renew@example.com");
        Assert.Equal(0, order.TaxMinor);
        var subscription = await SubscriptionOfAsync(order.OrderId);
        Assert.True(subscription.TaxExempt);

        var lines = await RenewAndReadAsync(subscription);

        Assert.DoesNotContain(lines, l => l.AccountCode == Tax(store));
        Assert.Equal(2_200, Credit(lines, Rev(store)));
        AssertWellFormed(lines, currency);
    }

    [Fact]
    public async Task A_zero_rate_store_renews_with_no_tax_line()
    {
        const string currency = "RTZ";
        var store = await StoreAsync(currency, bps: 0, inclusive: true);
        var subscription = await StartAsync(store, currency, priceMinor: 1_500, taxInclusive: true);

        var lines = await RenewAndReadAsync(subscription);

        Assert.DoesNotContain(lines, l => l.AccountCode == Tax(store)); // ck_line_one_side: no zero line
        Assert.Equal(1_500, Credit(lines, Rev(store)));
        AssertWellFormed(lines, currency);
    }

    [Fact]
    public async Task An_exclusive_store_renewal_is_unchanged_pending_the_owners_decision()
    {
        // US-style: checkout ADDS tax, but a renewal charges the bare price. Adding it would change what customers
        // pay, so it is an owner decision — until then: same charge, no tax posted (nothing was collected).
        const string currency = "RTU";
        var store = await StoreAsync(currency, bps: 825, inclusive: false);
        var subscription = await StartAsync(store, currency, priceMinor: 1_000, taxInclusive: false);

        var lines = await RenewAndReadAsync(subscription);

        Assert.Equal(1_000, CashDebits(lines));
        Assert.Equal(1_000, Credit(lines, Rev(store)));
        Assert.DoesNotContain(lines, l => l.AccountCode == Tax(store));
    }

    [Fact]
    public async Task Two_storefronts_sharing_a_currency_each_book_their_own_tax()
    {
        // Multi-storefront: one currency, two stores at different rates — each renewal's tax lands on its OWN
        // store's tax account at its OWN rate, never the other's (storefront-scoped tax, ADR-0055).
        const string currency = "RTM";
        var gst = await StoreAsync(currency, bps: 1000, inclusive: true);
        var zero = await StoreAsync(currency, bps: 0, inclusive: true);
        var gstSub = await StartAsync(gst, currency, priceMinor: 2_200, taxInclusive: true);
        var zeroSub = await StartAsync(zero, currency, priceMinor: 2_200, taxInclusive: true);

        var gstLines = await RenewAndReadAsync(gstSub);
        var zeroLines = await RenewAndReadAsync(zeroSub);

        Assert.Equal(200, Credit(gstLines, Tax(gst)));
        Assert.DoesNotContain(gstLines, l => l.AccountCode.Contains($"{zero:N}", StringComparison.Ordinal));
        Assert.DoesNotContain(zeroLines, l => l.AccountCode == Tax(zero));
        Assert.DoesNotContain(zeroLines, l => l.AccountCode.Contains($"{gst:N}", StringComparison.Ordinal));
        Assert.Equal(2_200, Credit(zeroLines, Rev(zero)));
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task A_usage_overage_charge_on_an_inclusive_store_posts_net_and_tax()
    {
        const string currency = "RTO";
        var store = await StoreAsync(currency, bps: 1000, inclusive: true);
        var reference = $"overage-{Guid.NewGuid():N}";

        await fixture.PublishAsync(new UsageOverageCharge(
            TenantId, "meter@example.com", MeterType.Token, OverageQuantity: 20, ChargeMinor: 1_100, currency, reference, store));

        var lines = await EntryLinesAsync(reference);
        Assert.Equal(100, Credit(lines, Tax(store)));
        Assert.Equal(1_000, Credit(lines, Rev(store)));
        Assert.Equal(1_100, CashDebits(lines)); // gross charged unchanged
        AssertWellFormed(lines, currency);
    }

    [Fact]
    public async Task A_usage_auto_load_charge_on_an_inclusive_store_posts_net_and_tax()
    {
        const string currency = "RTL";
        var store = await StoreAsync(currency, bps: 2000, inclusive: true);
        var reference = $"autoload-{Guid.NewGuid():N}";

        await fixture.PublishAsync(new UsageAutoLoadCharge(
            TenantId, "meter@example.com", MeterType.Token, ReloadQuantity: 100, ChargeMinor: 1_200, currency, reference, store));

        var lines = await EntryLinesAsync(reference);
        Assert.Equal(200, Credit(lines, Tax(store)));
        Assert.Equal(1_000, Credit(lines, Rev(store)));
        AssertWellFormed(lines, currency);
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private static long Credit(IEnumerable<JournalLine> lines, string account) =>
        lines.Where(l => l.AccountCode == account).Sum(l => l.CreditMinor);

    // The charge settles into the store's per-provider cash account (whichever provider the host resolves).
    private static long CashDebits(IEnumerable<JournalLine> lines) =>
        lines.Where(l => l.AccountCode.StartsWith("cash.", StringComparison.Ordinal)).Sum(l => l.DebitMinor);

    private static void AssertWellFormed(IReadOnlyCollection<JournalLine> lines, string currency)
    {
        Assert.Equal(lines.Sum(l => l.DebitMinor), lines.Sum(l => l.CreditMinor));         // balanced
        Assert.All(lines, l => Assert.Equal(currency, l.Currency));                          // every line has Currency
        Assert.All(lines, l => Assert.True((l.DebitMinor > 0) ^ (l.CreditMinor > 0)));      // ck_line_one_side
        Assert.DoesNotContain(lines, l => Accounts.IsSharedCode(l.AccountCode));            // ADR-0045 attribution
    }

    /// <summary>A live storefront projected into BOTH Ordering (checkout tax) and Payments (ledger codes + rate).</summary>
    private async Task<Guid> StoreAsync(string currency, int bps, bool inclusive, Guid? storefrontId = null)
    {
        var id = storefrontId ?? Guid.CreateVersion7();
        await fixture.PublishAsync(new StorefrontConfigChanged(
            id, TenantId, $"Tax Store {currency}", currency, bps, IsLive: true, TaxInclusive: inclusive,
            ReceivableAccountCode: $"receivable.store-{id:N}", RevenueAccountCode: Rev(id), TaxAccountCode: Tax(id),
            ShippingAccountCode: $"shipping.store-{id:N}"));
        await WaitAsync(async () =>
        {
            using var ordering = fixture.Ordering.Services.CreateScope();
            var copy = await ordering.ServiceProvider.GetRequiredService<OrderingDbContext>().StorefrontTaxCopies.AsNoTracking()
                .SingleOrDefaultAsync(c => c.StorefrontId == id);
            using var payments = fixture.Payments.Services.CreateScope();
            var accounts = await payments.ServiceProvider.GetRequiredService<PaymentsDbContext>().StorefrontLedgerAccounts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.StorefrontId == id);
            return copy is { IsLive: true } && copy.TaxRateBasisPoints == bps && copy.TaxInclusive == inclusive
                && accounts is not null && accounts.TaxRateBasisPoints == bps && accounts.TaxInclusive == inclusive;
        }, $"Storefront {id} did not project its tax terms");
        return id;
    }

    private async Task<Subscription> StartAsync(Guid store, string currency, long priceMinor, bool taxInclusive)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SubscriptionService>().StartAsync(new SubscriptionRequested(
            TenantId, Guid.CreateVersion7(), "renewal-tax@example.com", Guid.CreateVersion7(), null, BillingPeriod.Monthly,
            priceMinor, currency, store, taxInclusive), default);
    }

    private async Task<List<JournalLine>> RenewAndReadAsync(Subscription subscription)
    {
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var renewed = await scope.ServiceProvider.GetRequiredService<SubscriptionService>()
                .RenewAsync(subscription.TenantId, subscription.Id, default);
            Assert.NotNull(renewed);
            Assert.NotEqual(SubscriptionStatus.PastDue, renewed!.Status);
        }

        return await EntryLinesAsync($"renew-{subscription.Id}-2");
    }

    private async Task<List<JournalLine>> EntryLinesAsync(string reference)
    {
        List<JournalLine> lines = [];
        await WaitAsync(async () =>
        {
            using var scope = fixture.Payments.Services.CreateScope();
            var entry = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().JournalEntries
                .AsNoTracking().Include(e => e.Lines).SingleOrDefaultAsync(e => e.Reference == reference);
            lines = entry?.Lines.ToList() ?? [];
            return entry is not null;
        }, $"No ledger entry posted for {reference}");
        return lines;
    }

    private async Task<Subscription> SubscriptionOfAsync(Guid orderId)
    {
        Subscription? subscription = null;
        await WaitAsync(async () =>
        {
            using var scope = fixture.Payments.Services.CreateScope();
            subscription = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Subscriptions
                .AsNoTracking().FirstOrDefaultAsync(s => s.OrderId == orderId);
            return subscription is not null;
        }, $"Subscription for order {orderId} was never set up");
        return subscription!;
    }

    private async Task ExemptForGermanyAsync(Guid productId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var copy = await db.ProductCopies.SingleAsync(p => p.ProductId == productId);
        copy.ShipRules = [new ProductShipRule("DE", ChargeDestinationTax: false, ShippingCovered: false)];
        await db.SaveChangesAsync();
    }

    /// <summary>A verified member with a stored instrument buys the subscription; the payment then settles.</summary>
    private async Task<CheckoutResponseDto> SubscribeAsync(Guid storefrontId, Guid productId, string email)
    {
        using var client = fixture.Ordering.CreateClient();
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
        return order;
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
