using System.Net.Http.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Owner decisions 2026-10-27 (ADR-0057 amendment), end to end against real Postgres + RabbitMQ:
/// <list type="bullet">
/// <item><b>subscription_renewal_price_quantity</b> — a quantity-2 subscription line renews at unit × 2 − the line's
/// renewal discount: the cart's "renews at" preview, the subscription Payments stores, and the renewal charge agree,
/// and an exclusive store adds its tax on that total — the same tax checkout added to the first period.</item>
/// <item><b>subscription_renew_on_failed_charge</b> — a renewal the provider returns Failed / RequiresAction for is
/// dunned: PastDue committed, period not advanced, no renewal entry posted; RequiresAction publishes
/// <see cref="SubscriptionRenewalActionRequired"/> through the bus outbox; the retry re-sends the same key and pinned
/// amount and then renews exactly once.</item>
/// </list>
/// The rail is scripted (the host's mock always settles a renewal); everything else — the DbContext, the outbox, the
/// ledger, Ordering's checkout and confirmation — is the real host's.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class SubscriptionRenewalDunningTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private sealed record CheckoutResponseDto(
        Guid OrderId, string? ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
        long GrossMinor, string Currency, string? Message);

    private sealed record CartSummaryDto(long SubtotalMinor, List<CartRenewalDto>? Renewals = null);

    private sealed record CartRenewalDto(int BillingPeriod, long AmountMinor, bool TaxAdded);

    [Fact]
    public async Task A_two_unit_subscription_previews_stores_and_renews_at_the_line_total_with_the_tax_on_that_total()
    {
        const string currency = "RQ1";
        var store = await StoreAsync(currency, bps: 825, inclusive: false);
        var productId = await fixture.SeedRecurringProductAsync(1_000, currency);
        await PermanentPromotionAsync(store, currency, productId, percentOff: 25);

        // The preview: 2 × 1000 less the 25% that rides renewals = 1500 per month (+ tax).
        using (var cart = fixture.Ordering.CreateClient())
        {
            cart.DefaultRequestHeaders.Add("X-3C-Storefront-Id", store.ToString());
            (await cart.PostAsJsonAsync("/cart/items", new { productId, quantity = 2 })).EnsureSuccessStatusCode();
            var summary = (await cart.GetFromJsonAsync<CartSummaryDto>($"/cart/summary?storefrontId={store}"))!;
            var preview = Assert.Single(summary.Renewals ?? []);
            Assert.Equal(1_500, preview.AmountMinor);
            Assert.True(preview.TaxAdded);
        }

        var order = await SubscribeAsync(store, productId, quantity: 2, "qty-renew@example.com");
        Assert.Equal(500, order.DiscountMinor);   // the first period: 2000 − 25%
        Assert.Equal(124, order.TaxMinor);        // 8.25% of 1500 = 123.75 → 124, ADDED

        var subscription = await SubscriptionOfAsync(order.OrderId);
        Assert.Equal(1_500, subscription.PriceMinor); // the preview's figure (was 500: ONE unit less the whole line's discount)
        Assert.Equal(2, subscription.Quantity);

        var provider = new ScriptedProvider();
        await RenewAsync(subscription.Id, provider);

        Assert.Equal(1_624, Assert.Single(provider.Requests).AmountMinor);   // 1500 + the tax checkout added
        var lines = await EntryLinesAsync($"renew-{subscription.Id}-2");
        Assert.Equal(order.TaxMinor, lines.Where(l => l.AccountCode == $"tax.store-{store:N}").Sum(l => l.CreditMinor));
        Assert.Equal(1_500, lines.Where(l => l.AccountCode == $"revenue.store-{store:N}").Sum(l => l.CreditMinor));
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task A_renewal_needing_authentication_duns_asks_the_customer_and_its_retry_renews_once_at_the_same_amount()
    {
        const string currency = "RQ2";
        var store = await StoreAsync(currency, bps: 825, inclusive: false);
        var subscription = await StartAsync(store, currency, priceMinor: 2_000, quantity: 2);

        var asked = new TaskCompletionSource<SubscriptionRenewalActionRequired>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(new Uri(fixture.RabbitMqUri));
            cfg.ReceiveEndpoint($"renewal-action-probe-{Guid.NewGuid():N}", e =>
            {
                e.AutoDelete = true;
                e.Durable = false;
                e.Handler<SubscriptionRenewalActionRequired>(ctx =>
                {
                    if (ctx.Message.SubscriptionId == subscription.Id)
                    {
                        asked.TrySetResult(ctx.Message);
                    }

                    return Task.CompletedTask;
                });
            });
        });
        await probe.StartAsync();
        try
        {
            var provider = new ScriptedProvider(PaymentOutcome.RequiresAction);
            var dunned = await RenewAsync(subscription.Id, provider);
            Assert.Equal(SubscriptionStatus.PastDue, dunned.Status);

            var stored = await StoredAsync(subscription.Id);
            Assert.Equal(SubscriptionStatus.PastDue, stored.Status);                    // committed
            Assert.Equal(subscription.CurrentPeriodEnd, stored.CurrentPeriodEnd);        // period NOT advanced
            Assert.Single(stored.Renewals);                                              // no history row
            Assert.False(await EntryExistsAsync($"renew-{subscription.Id}-2"));          // no revenue, no tax

            // Delivered through the bus outbox with the dunning commit.
            var message = await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(2_165, message.AmountMinor);                                    // 2000 + 8.25% (165), pinned
            Assert.Equal(subscription.CurrentPeriodEnd, message.PeriodEnd);
            Assert.Equal("renewal-dunning@example.com", message.CustomerEmail);
            Assert.Equal(store, message.StorefrontId);

            // The customer authenticates; the retry is the same period: same key, same amount — and renews once.
            provider.Outcome = PaymentOutcome.Succeeded;
            var renewed = await RenewAsync(subscription.Id, provider);
            Assert.Equal(SubscriptionStatus.Active, renewed.Status);
            Assert.Equal(2, provider.Requests.Count);
            Assert.Equal(provider.Requests[0].IdempotencyKey, provider.Requests[1].IdempotencyKey);
            Assert.All(provider.Requests, r => Assert.Equal(2_165, r.AmountMinor));
            var lines = await EntryLinesAsync($"renew-{subscription.Id}-2");
            Assert.Equal(165, lines.Where(l => l.AccountCode == $"tax.store-{store:N}").Sum(l => l.CreditMinor));
            Assert.Equal(subscription.CurrentPeriodEnd, (await StoredAsync(subscription.Id)).CurrentPeriodStart);
        }
        finally
        {
            await probe.StopAsync();
        }
    }

    [Fact]
    public async Task A_declined_renewal_duns_without_advancing_or_posting()
    {
        const string currency = "RQ3";
        var store = await StoreAsync(currency, bps: 1000, inclusive: true);
        var subscription = await StartAsync(store, currency, priceMinor: 2_200, quantity: 1);

        var dunned = await RenewAsync(subscription.Id, new ScriptedProvider(PaymentOutcome.Failed));

        Assert.Equal(SubscriptionStatus.PastDue, dunned.Status);
        var stored = await StoredAsync(subscription.Id);
        Assert.Equal(SubscriptionStatus.PastDue, stored.Status);
        Assert.Equal(subscription.CurrentPeriodStart, stored.CurrentPeriodStart);
        Assert.Equal(subscription.CurrentPeriodEnd, stored.CurrentPeriodEnd);
        Assert.Single(stored.Renewals);
        Assert.False(await EntryExistsAsync($"renew-{subscription.Id}-2"));
    }

    /// <summary>
    /// Postgres keeps microseconds, a Linux clock 100 ns ticks. A subscription started at …54.1755249 and dunned in the
    /// SAME DbContext keys its renewal from the tracked aggregate; the retry reloads the row (…54.1755240). Both must be
    /// one key with one pinned amount — otherwise the retry is a second charge request under a new key. The clock is
    /// pinned to the sub-microsecond tick so this reproduces on macOS too (whose clock only gives microseconds).
    /// </summary>
    [Fact]
    public async Task A_renewal_keyed_before_a_reload_is_retried_under_the_same_key_after_it()
    {
        const string currency = "RQ4";
        var store = await StoreAsync(currency, bps: 825, inclusive: false);
        var linuxNow = new DateTimeOffset(2026, 11, 27, 15, 47, 54, TimeSpan.Zero).AddTicks(1_755_249);
        var provider = new ScriptedProvider(PaymentOutcome.RequiresAction);

        Subscription subscription;
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var service = Service(scope.ServiceProvider, provider, new FixedClock(linuxNow));
            subscription = await service.StartAsync(new SubscriptionRequested(
                TenantId, Guid.CreateVersion7(), "renewal-precision@example.com", Guid.CreateVersion7(), null,
                BillingPeriod.Monthly, 2_000, currency, store, TaxInclusive: null, TaxExempt: false, Quantity: 1), default);
            var dunned = await service.RenewAsync(TenantId, subscription.Id, default); // the tracked, never-reloaded aggregate
            Assert.Equal(SubscriptionStatus.PastDue, dunned!.Status);
        }

        var stored = await StoredAsync(subscription.Id);
        Assert.Equal(subscription.CurrentPeriodEnd, stored.CurrentPeriodEnd);   // in memory == what Postgres kept
        Assert.Equal(subscription.CurrentPeriodStart, stored.CurrentPeriodStart);

        provider.Outcome = PaymentOutcome.Succeeded;
        var renewed = await RenewAsync(subscription.Id, provider);              // a fresh context: the reloaded row
        Assert.Equal(SubscriptionStatus.Active, renewed.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(provider.Requests[0].IdempotencyKey, provider.Requests[1].IdempotencyKey);
        Assert.All(provider.Requests, r => Assert.Equal(2_165, r.AmountMinor));

        using var check = fixture.Payments.Services.CreateScope();
        var pins = await check.ServiceProvider.GetRequiredService<PaymentsDbContext>().IdempotencyRecords.AsNoTracking()
            .CountAsync(r => r.Key.StartsWith($"charge-amount:renew-{subscription.Id}-"));
        Assert.Equal(1, pins);                                                  // one period, one pinned amount
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    /// <summary>Renew through the real host's DbContext, mode resolver and bus-outbox publisher — only the rail is scripted.</summary>
    private async Task<Subscription> RenewAsync(Guid subscriptionId, ScriptedProvider provider)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        return (await Service(scope.ServiceProvider, provider, TimeProvider.System).RenewAsync(TenantId, subscriptionId, default))!;
    }

    private static SubscriptionService Service(IServiceProvider sp, ScriptedProvider provider, TimeProvider clock) =>
        new(sp.GetRequiredService<PaymentsDbContext>(), new ScriptedRegistry(provider), sp.GetRequiredService<PaymentModeResolver>(),
            clock, sp.GetRequiredService<ILogger<SubscriptionService>>(), sp.GetRequiredService<IPublishEndpoint>());

    private async Task<Subscription> StoredAsync(Guid subscriptionId)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Subscriptions.AsNoTracking()
            .Include(s => s.Renewals).SingleAsync(s => s.Id == subscriptionId);
    }

    private async Task<bool> EntryExistsAsync(string reference)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().JournalEntries.AnyAsync(e => e.Reference == reference);
    }

    private async Task<List<JournalLine>> EntryLinesAsync(string reference)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var entry = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().JournalEntries
            .AsNoTracking().Include(e => e.Lines).SingleAsync(e => e.Reference == reference);
        return entry.Lines.ToList();
    }

    private async Task<Subscription> StartAsync(Guid store, string currency, long priceMinor, int quantity)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SubscriptionService>().StartAsync(new SubscriptionRequested(
            TenantId, Guid.CreateVersion7(), "renewal-dunning@example.com", Guid.CreateVersion7(), null,
            BillingPeriod.Monthly, priceMinor, currency, store,
            TaxInclusive: null, TaxExempt: false, Quantity: quantity), default);
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
            id, TenantId, $"Dunning Store {currency}", currency, bps, IsLive: true, TaxInclusive: inclusive,
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

    private async Task PermanentPromotionAsync(Guid storefrontId, string currency, Guid productId, int percentOff)
    {
        var promotionId = Guid.CreateVersion7();
        await fixture.PublishAsync(new PromotionChanged(
            promotionId, TenantId, storefrontId, "Forever promo", currency,
            PromotionScopeKind.Product, ProductId: productId,
            MinimumAmountMinor: 0, MinimumQuantity: 0,
            GrantsFreeShipping: false, PercentOff: percentOff, DiscountAmountMinor: 0,
            Combinable: true, Active: true, ActiveFrom: null, ActiveUntil: null,
            Code: null, MaxRedemptions: null, MaxRedemptionsPerCustomer: null,
            AppliesToRenewals: true));
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.PromotionCopies.AsNoTracking().AnyAsync(p => p.PromotionId == promotionId && p.AppliesToRenewals);
        }, $"Promotion {promotionId} did not project");
    }

    /// <summary>A verified member with a stored instrument buys <paramref name="quantity"/> units; the payment then settles.</summary>
    private async Task<CheckoutResponseDto> SubscribeAsync(Guid storefrontId, Guid productId, int quantity, string email)
    {
        using var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
        client.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName,
            fixture.MintInternalClaims(Guid.NewGuid(), "customer", email, emailVerified: true));

        (await client.PostAsJsonAsync("/cart/items", new { productId, quantity })).EnsureSuccessStatusCode();
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

    // Awake-time budget (Eventually): a Mac sleep mid-wait cannot expire it (engineering-gotchas, integration waits).
    private static Task WaitAsync(Func<Task<bool>> settled, string what) =>
        Eventually.UntilAsync(settled, () => new TimeoutException($"{what}."), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250));

    /// <summary>A rail that answers <see cref="Outcome"/> and records what it was asked to charge.</summary>
    private sealed class ScriptedProvider(PaymentOutcome outcome = PaymentOutcome.Succeeded) : IPaymentProvider
    {
        public PaymentOutcome Outcome { get; set; } = outcome;

        public List<PaymentRequest> Requests { get; } = [];

        public string ProviderKey => "mock";

        public Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var error = Outcome switch
            {
                PaymentOutcome.RequiresAction => new PaymentError(PaymentErrorCode.AuthenticationRequired, "3DS required.", Retryable: true),
                PaymentOutcome.Failed => new PaymentError(PaymentErrorCode.CardDeclined, "Card declined.", Retryable: false),
                _ => null,
            };
            return Task.FromResult(new PaymentResponse($"pi_scripted_{Requests.Count}", null, Outcome, error));
        }

        public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) => throw new NotSupportedException();
        public Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets) => null;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ScriptedRegistry(IPaymentProvider provider) : IPaymentProviderRegistry
    {
        public IPaymentProvider Resolve(PaymentAccountSnapshot account) => provider;
        public IPaymentProvider ResolveDefault() => provider;
        public IPaymentProvider ResolveByKey(string providerKey) => provider;
    }
}
