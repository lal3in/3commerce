using System.Net;
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
/// renewal_usage_refund_path (owner decision 2026-10-27, ADR-0041/0057 amendments 2026-10-28), end to end against real
/// Postgres + RabbitMQ on the real hosts (LocalMock rail): a settled renewal / usage charge is a Payment of its own — the
/// filtered unique index lets a renewal share its order with the order's checkout payment — so the admin refund
/// (<c>POST /admin/refunds</c> with <c>chargeReference</c>) → <see cref="RefundRequested"/> → the single refund consumer,
/// partial refunds and the dispute webhook reach it; each reversal takes that charge's own tax, so the tax account nets to
/// zero; and Ordering leaves the order alone (Confirmed, not refunded, not disputed).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class RecurringChargeRefundTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private sealed record CheckoutResponseDto(Guid OrderId, long NetMinor, long TaxMinor, long GrossMinor, string Currency);

    private sealed record RenewalRowDto(int Sequence, long AmountMinor, string? ChargeReference, string? PaymentIntentId, long? TaxMinor, long? RefundedMinor);

    private sealed record SubscriptionDetailDto(Guid Id, List<RenewalRowDto> Renewals);

    [Fact]
    public async Task A_renewal_is_refunded_by_its_charge_reference_its_tax_nets_to_zero_and_the_order_stands()
    {
        const string currency = "RF1";
        var store = await StoreAsync(currency, bps: 825, inclusive: false);
        var productId = await fixture.SeedRecurringProductAsync(1_000, currency);
        var order = await SubscribeAsync(store, productId, "renewal-refund@example.com");
        await WaitAsync(async () => await OrderStatusAsync(order.OrderId) == OrderStatus.Confirmed, "order did not confirm");
        var subscription = await SubscriptionOfAsync(order.OrderId);

        await RenewOnTheHostAsync(subscription.Id);                                   // LocalMock rail: settles
        var reference = $"renew-{subscription.Id}-2";

        // The renewal's own payment shares the order with the checkout payment (filtered unique index, real Postgres).
        var renewal = await PaymentAsync(p => p.ChargeReference == reference);
        var checkout = await PaymentAsync(p => p.OrderId == order.OrderId && p.Kind == PaymentKind.Order);
        Assert.Equal((PaymentKind.Renewal, order.OrderId, 1_083L, 83L), (renewal.Kind, renewal.OrderId, renewal.AmountMinor, renewal.TaxMinor));
        Assert.NotEqual(checkout.PaymentIntentId, renewal.PaymentIntentId);

        // Admin sees which charge each period was, and refunds it by that reference.
        using var admin = Admin();
        var detail = (await admin.GetFromJsonAsync<SubscriptionDetailDto>($"/admin/subscriptions/{subscription.Id}"))!;
        var period2 = detail.Renewals.Single(r => r.Sequence == 2);
        Assert.Equal((reference, renewal.PaymentIntentId, 83L, 0L), (period2.ChargeReference, period2.PaymentIntentId, period2.TaxMinor!.Value, period2.RefundedMinor!.Value));
        Assert.Null(detail.Renewals.Single(r => r.Sequence == 1).ChargeReference);   // period 1 is the order's payment

        var refundId = await RefundAsync(admin, order.OrderId, 1_083, reference);
        await WaitAsync(async () => (await PaymentAsync(p => p.ChargeReference == reference)).Status == PaymentStatus.Refunded, "renewal not refunded");

        var sale = await EntryAsync(reference);
        var refund = await EntryAsync(refundId.ToString());
        foreach (var account in sale.Lines.Concat(refund.Lines).Select(l => l.AccountCode).Distinct())
        {
            Assert.Equal(0, Net(sale, account) + Net(refund, account));                // tax.store-… included
        }

        Assert.Equal(83, refund.Lines.Where(l => l.AccountCode == $"tax.store-{store:N}").Sum(l => l.DebitMinor));
        Assert.Equal(0, (await PaymentAsync(p => p.Id == checkout.Id)).RefundedMinor); // the checkout payment stands

        // Ordering consumes refunds per order in sequence: once a later small ORDER refund has flagged the order partially
        // refunded, the renewal's (full) refund was consumed before it — and must not have moved the order to Refunded.
        await RefundAsync(admin, order.OrderId, 1, chargeReference: null);
        await WaitAsync(async () => (await OrderAsync(order.OrderId)).PartiallyRefunded, "order refund not applied");
        Assert.Equal(OrderStatus.Confirmed, (await OrderAsync(order.OrderId)).Status);
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task A_dispute_on_a_renewal_intent_reverses_that_charge_under_its_own_reference()
    {
        const string currency = "RF2";
        var store = await StoreAsync(currency, bps: 1000, inclusive: true);
        var productId = await fixture.SeedRecurringProductAsync(2_200, currency);
        var order = await SubscribeAsync(store, productId, "renewal-dispute@example.com");
        var subscription = await SubscriptionOfAsync(order.OrderId);
        await RenewOnTheHostAsync(subscription.Id);
        var reference = $"renew-{subscription.Id}-2";
        var renewal = await PaymentAsync(p => p.ChargeReference == reference);

        using (var payments = fixture.Payments.CreateClient())
        {
            (await payments.PostAsync($"/dev/simulate-chargeback/{renewal.PaymentIntentId}?feeMinor=1500", null)).EnsureSuccessStatusCode();
        }

        var chargeback = await EntryAsync($"{reference}:chargeback");
        Assert.Equal(200, chargeback.Lines.Where(l => l.AccountCode == $"tax.store-{store:N}").Sum(l => l.DebitMinor)); // GST of 2200
        Assert.Equal(PaymentStatus.Disputed, (await PaymentAsync(p => p.Id == renewal.Id)).Status);
        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        Assert.False(await db.JournalEntries.AnyAsync(e => e.Reference == $"{order.OrderId}:chargeback"));        // not the order's
        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(p => p.OrderId == order.OrderId && p.Kind == PaymentKind.Order)).Status);

        // Ordering consumes per order in sequence: once a later ORDER refund has flagged the order, the renewal's
        // PaymentDisputed was consumed before it — and must not have badged the order Disputed.
        using var admin = Admin();
        await RefundAsync(admin, order.OrderId, 1, chargeReference: null);
        await WaitAsync(async () => (await OrderAsync(order.OrderId)).PartiallyRefunded, "order refund not applied");
        Assert.False((await OrderAsync(order.OrderId)).Disputed);
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task A_usage_charge_is_refunded_in_parts_by_its_reference_with_its_own_tax()
    {
        const string currency = "RF3";
        var store = await StoreAsync(currency, bps: 825, inclusive: false);
        var reference = $"overage-{Guid.NewGuid():N}";
        await fixture.PublishAsync(new UsageOverageCharge(
            TenantId, "meter-refund@example.com", MeterType.Token, OverageQuantity: 20, ChargeMinor: 1_000, currency, reference, store));
        await WaitAsync(async () => await TryPaymentAsync(p => p.ChargeReference == reference) is not null, "usage payment not recorded");
        var usage = await PaymentAsync(p => p.ChargeReference == reference);
        Assert.Equal((PaymentKind.UsageCharge, Guid.Empty, 1_083L, 83L), (usage.Kind, usage.OrderId, usage.AmountMinor, usage.TaxMinor));

        using var admin = Admin();
        var first = await RefundAsync(admin, Guid.Empty, 500, reference);
        await WaitAsync(async () => (await PaymentAsync(p => p.Id == usage.Id)).RefundedMinor == 500, "first part not refunded");
        var second = await RefundAsync(admin, Guid.Empty, 583, reference);
        await WaitAsync(async () => (await PaymentAsync(p => p.Id == usage.Id)).Status == PaymentStatus.Refunded, "usage not refunded");

        var tax = $"tax.store-{store:N}";
        var taxBack = (await EntryAsync(first.ToString())).Lines.Concat((await EntryAsync(second.ToString())).Lines)
            .Where(l => l.AccountCode == tax).Sum(l => l.DebitMinor);
        Assert.Equal(83, taxBack);                                                      // 38 + 45 = the charge's own 83
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    private HttpClient Admin()
    {
        var client = fixture.Payments.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));
        return client;
    }

    private static async Task<Guid> RefundAsync(HttpClient admin, Guid orderId, long amount, string? chargeReference)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/refunds")
        {
            Content = JsonContent.Create(new { orderId, amountMinor = amount, reason = "recurring charge refund", chargeReference }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RefundIdDto>())!.RefundId;
    }

    private sealed record RefundIdDto(Guid RefundId);

    /// <summary>The operator's Renew on the real host: its DbContext, its LocalMock rail, its outbox.</summary>
    private async Task RenewOnTheHostAsync(Guid subscriptionId)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var renewed = await scope.ServiceProvider.GetRequiredService<SubscriptionService>().RenewAsync(TenantId, subscriptionId, default);
        Assert.Equal(SubscriptionStatus.Active, renewed!.Status);
    }

    private async Task<Payment?> TryPaymentAsync(System.Linq.Expressions.Expression<Func<Payment, bool>> match)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Payments.AsNoTracking().SingleOrDefaultAsync(match);
    }

    private async Task<Payment> PaymentAsync(System.Linq.Expressions.Expression<Func<Payment, bool>> match) =>
        (await TryPaymentAsync(match)) ?? throw new InvalidOperationException("payment not found");

    private async Task<JournalEntry> EntryAsync(string reference)
    {
        JournalEntry? entry = null;
        await WaitAsync(async () =>
        {
            using var scope = fixture.Payments.Services.CreateScope();
            entry = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().JournalEntries
                .AsNoTracking().Include(e => e.Lines).SingleOrDefaultAsync(e => e.Reference == reference);
            return entry is not null;
        }, $"journal entry {reference} was never posted");
        return entry!;
    }

    private static long Net(JournalEntry entry, string account) =>
        entry.Lines.Where(l => l.AccountCode == account).Sum(l => l.DebitMinor - l.CreditMinor);

    private async Task<Order> OrderAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
    }

    private async Task<OrderStatus?> OrderStatusAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Orders.AsNoTracking()
            .Where(o => o.Id == orderId).Select(o => (OrderStatus?)o.Status).SingleOrDefaultAsync();
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

    /// <summary>A live storefront projected into BOTH Ordering (checkout tax) and Payments (ledger codes + rate).</summary>
    private async Task<Guid> StoreAsync(string currency, int bps, bool inclusive)
    {
        var id = Guid.CreateVersion7();
        await fixture.PublishAsync(new StorefrontConfigChanged(
            id, TenantId, $"Refund Store {currency}", currency, bps, IsLive: true, TaxInclusive: inclusive,
            ReceivableAccountCode: $"receivable.store-{id:N}", RevenueAccountCode: $"revenue.store-{id:N}",
            TaxAccountCode: $"tax.store-{id:N}", ShippingAccountCode: $"shipping.store-{id:N}"));
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

    /// <summary>A verified member with a stored instrument buys one unit; the payment then settles.</summary>
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
        (await payments.PostAsync($"/dev/simulate-payment/pi_fake_{order.OrderId:N}?amountMinor={order.GrossMinor}", null))
            .EnsureSuccessStatusCode();
        return order;
    }

    // Awake-time budget (Eventually): a Mac sleep mid-wait cannot expire it (engineering-gotchas, integration waits).
    private static Task WaitAsync(Func<Task<bool>> settled, string what) =>
        Eventually.UntilAsync(settled, () => new TimeoutException($"{what}."), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250));
}
