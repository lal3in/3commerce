using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Contracts.Support;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// The offer that SUPPLIES an order line comes from the sell gate's set (ADR-0059, supply_offer_sell_gate):
/// approved, active offers for THIS storefront (or all storefronts) in the ORDER'S currency; within it a
/// variant-specific offer beats a product-level one, then lowest Priority. Supplier, fulfilment type, billing
/// mode and the booked supplier cost all come from that one offer, and a reversal reverses exactly what was
/// booked. Every case is built so the pre-fix resolver (variant, then Priority, ignoring store and currency)
/// would have picked a DIFFERENT offer — the negative control.
/// <para>
/// Fabricated currency codes (SG1…SG4) keep these stores and products apart from the rest of the shared
/// Phase-3 databases (the same trick as <see cref="SubscriptionRenewalPriceTests"/>).
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class SupplyOfferSellGateTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private sealed record CheckoutResponseDto(
        Guid OrderId, string? ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
        long GrossMinor, string Currency, string? Message);

    [Fact]
    public async Task An_order_on_store_A_books_ships_and_reverses_store_As_offer_not_store_Bs()
    {
        const string currency = "SG1";
        var storeA = await LiveStorefrontAsync(currency);
        var storeB = await LiveStorefrontAsync(currency);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        var supplierA = Guid.CreateVersion7();
        var supplierB = Guid.CreateVersion7();
        await fixture.ApproveSupplierAsync(supplierA);
        await fixture.ApproveSupplierAsync(supplierB);

        // Store B's offer has the better Priority, so the old store-blind resolver chose it for store A too.
        var offerA = await fixture.PublishOfferAsync(
            productId, supplierA, currency, supplierCostMinor: 3_000, priority: 5, storefrontId: storeA,
            fulfilmentType: FulfilmentType.Dropship);
        await fixture.PublishOfferAsync(
            productId, supplierB, currency, supplierCostMinor: 4_500, priority: 0, storefrontId: storeB,
            fulfilmentType: FulfilmentType.DigitalDownload);

        // Store B's own checkout is supplied by store B's offer (snapshotted on the attempt line).
        var orderOnB = await CheckoutAsync(storeB, productId, quantity: 1);
        var lineOnB = await AttemptLineAsync(orderOnB.OrderId);
        Assert.Equal(supplierB, lineOnB.SupplierId);
        Assert.Equal(FulfilmentType.DigitalDownload, lineOnB.FulfilmentType);
        Assert.Equal(4_500, lineOnB.SupplierUnitCostMinor);

        // Store A: supplier, fulfilment and cost all come from store A's offer.
        var order = await CheckoutAsync(storeA, productId, quantity: 2);
        Assert.Equal(currency, order.Currency);
        await PayAsync(order);
        await WaitForSupplierPayableAsync(order.OrderId);

        var line = await OrderLineAsync(order.OrderId);
        Assert.Equal(supplierA, line.SupplierId);
        Assert.Equal(FulfilmentType.Dropship, line.FulfilmentType);
        Assert.Equal(3_000, line.SupplierUnitCostMinor);
        Assert.Equal(6_000, line.BookedSupplierCostMinor);

        // What Fulfillment receives (the one OrderConfirmed mapping) ships via store A's supply.
        var shipped = Assert.Single(await ConfirmedLinesAsync(order.OrderId));
        Assert.Equal(supplierA, shipped.SupplierId);
        Assert.Equal(FulfilmentType.Dropship, shipped.FulfilmentType);

        // The accrual: store A's supplier and cost, in the order's currency, to store A's own accounts.
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var payable = await db.SupplierPayables.AsNoTracking().SingleAsync(p => p.OrderId == order.OrderId);
            Assert.Equal(supplierA, payable.SupplierEntityId);
            Assert.Equal(6_000, payable.GrossMinor);
            Assert.Equal(currency, payable.Currency);

            var lines = await EntryLinesAsync(db, payable.Id.ToString());
            Assert.Equal(6_000, lines.Where(l => l.AccountCode == Accounts.CogsStoreFor(storeA)).Sum(l => l.DebitMinor));
            Assert.Equal(6_000, lines.Where(l => l.AccountCode == Accounts.SupplierPayableStoreFor(storeA)).Sum(l => l.CreditMinor));
            Assert.All(lines, l => Assert.Equal(currency, l.Currency));
            Assert.Equal(lines.Sum(l => l.DebitMinor), lines.Sum(l => l.CreditMinor));
        }

        // The offer changes after the sale (cost tripled, demoted). A reversal must still reverse what THIS
        // order booked — never a value re-derived from today's offers.
        await fixture.PublishOfferAsync(
            productId, supplierA, currency, supplierCostMinor: 9_999, priority: 50, storefrontId: storeA,
            fulfilmentType: FulfilmentType.Dropship, offerId: offerA);

        // Restock RMA for half the order's gross → half the booked 6,000 is reversed (banker's rounding, as
        // Ordering values it). Re-deriving from the edited offer would have valued it at ~9,999 (capped 6,000).
        var refunded = order.GrossMinor / 2;
        var expected = (long)Math.Round(6_000m * refunded / order.GrossMinor, MidpointRounding.ToEven);
        var rmaId = Guid.CreateVersion7();
        await fixture.PublishAsync(new RmaDispositionSet(
            rmaId, order.OrderId, Kind: 1, StorageReason: null, Revision: 1, RefundedMinor: refunded));

        await WaitForEntryAsync($"{rmaId}:1");
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var restock = await EntryLinesAsync(db, $"{rmaId}:1");
            Assert.Equal(expected, restock.Where(l => l.AccountCode == Accounts.SupplierPayableStoreFor(storeA)).Sum(l => l.DebitMinor));
            Assert.Equal(expected, restock.Where(l => l.AccountCode == Accounts.CogsStoreFor(storeA)).Sum(l => l.CreditMinor));
            Assert.All(restock, l => Assert.Equal(currency, l.Currency));
            Assert.Equal(restock.Sum(l => l.DebitMinor), restock.Sum(l => l.CreditMinor));
        }

        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task One_offer_decides_price_supply_and_cost_store_As_own_offer_on_A_the_all_store_offer_on_B()
    {
        // The owner's example (2026-10-15): an all-store offer (Priority 1, supplier X, cost 6, price 10) and a
        // store-A-only offer (Priority 2, supplier Y, cost 8, price 12). Store A charges 12, ships via Y and
        // books 8; store B charges 10, ships via X and books 6. Before the fix store A charged Y's 12 but
        // shipped and costed X (the supply resolver ignored the store and picked Priority 1).
        const string currency = "SG5";
        var storeA = await LiveStorefrontAsync(currency);
        var storeB = await LiveStorefrontAsync(currency);
        var productId = await fixture.SeedProductAsync(999, currency);

        var x = Guid.CreateVersion7();
        var y = Guid.CreateVersion7();
        await fixture.ApproveSupplierAsync(x);
        await fixture.ApproveSupplierAsync(y);
        await fixture.PublishOfferAsync(
            productId, x, currency, supplierCostMinor: 6, priority: 1, fulfilmentType: FulfilmentType.Dropship, priceMinor: 10);
        await fixture.PublishOfferAsync(
            productId, y, currency, supplierCostMinor: 8, priority: 2, storefrontId: storeA,
            fulfilmentType: FulfilmentType.Warehouse, priceMinor: 12);

        var onA = await CheckoutAsync(storeA, productId, quantity: 1);
        var onB = await CheckoutAsync(storeB, productId, quantity: 1);
        Assert.Equal(12, onA.NetMinor);
        Assert.Equal(10, onB.NetMinor);

        await PayAsync(onA);
        await PayAsync(onB);
        await WaitForSupplierPayableAsync(onA.OrderId);
        await WaitForSupplierPayableAsync(onB.OrderId);

        var lineA = await OrderLineAsync(onA.OrderId);
        Assert.Equal((12L, y, FulfilmentType.Warehouse, 8L, 8L),
            (lineA.UnitPriceMinor, lineA.SupplierId!.Value, lineA.FulfilmentType, lineA.SupplierUnitCostMinor, lineA.BookedSupplierCostMinor));
        var lineB = await OrderLineAsync(onB.OrderId);
        Assert.Equal((10L, x, FulfilmentType.Dropship, 6L, 6L),
            (lineB.UnitPriceMinor, lineB.SupplierId!.Value, lineB.FulfilmentType, lineB.SupplierUnitCostMinor, lineB.BookedSupplierCostMinor));

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            foreach (var (order, store, supplier, cost) in new[] { (onA, storeA, y, 8L), (onB, storeB, x, 6L) })
            {
                var payable = await db.SupplierPayables.AsNoTracking().SingleAsync(p => p.OrderId == order.OrderId);
                Assert.Equal(supplier, payable.SupplierEntityId);
                Assert.Equal(cost, payable.GrossMinor);
                Assert.Equal(currency, payable.Currency);

                var lines = await EntryLinesAsync(db, payable.Id.ToString());
                Assert.Equal(cost, lines.Where(l => l.AccountCode == Accounts.CogsStoreFor(store)).Sum(l => l.DebitMinor));
                Assert.All(lines, l => Assert.Equal(currency, l.Currency));
                Assert.Equal(lines.Sum(l => l.DebitMinor), lines.Sum(l => l.CreditMinor));
            }
        }

        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task The_order_currencys_offer_supplies_and_costs_the_line_not_a_cheaper_ranked_foreign_one()
    {
        const string currency = "SG2";
        var store = await LiveStorefrontAsync(currency);
        var productId = await fixture.SeedProductAsync(10_000, currency);

        var foreignSupplier = Guid.CreateVersion7();
        var localSupplier = Guid.CreateVersion7();
        await fixture.ApproveSupplierAsync(foreignSupplier);
        await fixture.ApproveSupplierAsync(localSupplier);

        // Both all-storefront; the SG3 offer ranks first on Priority, so the old resolver picked it and
        // relabelled its 4,000 into SG2. It cannot supply an SG2 order.
        await fixture.PublishOfferAsync(productId, foreignSupplier, "SG3", supplierCostMinor: 4_000, priority: 0);
        await fixture.PublishOfferAsync(productId, localSupplier, currency, supplierCostMinor: 2_500, priority: 9);

        var order = await CheckoutAsync(store, productId, quantity: 3);
        await PayAsync(order);
        await WaitForSupplierPayableAsync(order.OrderId);

        var line = await OrderLineAsync(order.OrderId);
        Assert.Equal(localSupplier, line.SupplierId);
        Assert.Equal(7_500, line.BookedSupplierCostMinor);

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var payable = await db.SupplierPayables.AsNoTracking().SingleAsync(p => p.OrderId == order.OrderId);
            Assert.Equal(localSupplier, payable.SupplierEntityId);
            Assert.Equal(7_500, payable.GrossMinor);
            Assert.Equal(currency, payable.Currency);

            var lines = await EntryLinesAsync(db, payable.Id.ToString());
            Assert.Equal(7_500, lines.Where(l => l.AccountCode == Accounts.CogsStoreFor(store)).Sum(l => l.DebitMinor));
            Assert.All(lines, l => Assert.Equal(currency, l.Currency));
            Assert.Equal(lines.Sum(l => l.DebitMinor), lines.Sum(l => l.CreditMinor));
        }

        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task A_subscription_supplied_by_this_stores_offer_still_renews_in_its_currency()
    {
        // Money-seam check (renewals): billing mode comes from the supply offer too. Another store's one-time
        // offer ranks first on Priority — the old resolver made this line one-time, so no subscription was
        // ever set up. Under the rule the all-store subscription offer supplies it, and the renewal (which
        // never passes through checkout) charges and books the stored price in the stored currency.
        const string currency = "SG4";
        var store = await LiveStorefrontAsync(currency);
        var otherStore = await LiveStorefrontAsync(currency);
        var productId = await fixture.SeedProductAsync(2_000, currency);

        var oneTimeSupplier = Guid.CreateVersion7();
        var subscriptionSupplier = Guid.CreateVersion7();
        await fixture.ApproveSupplierAsync(oneTimeSupplier);
        await fixture.ApproveSupplierAsync(subscriptionSupplier);
        await fixture.PublishOfferAsync(
            productId, oneTimeSupplier, currency, supplierCostMinor: 0, priority: 0, storefrontId: otherStore,
            fulfilmentType: FulfilmentType.DigitalDownload);
        await fixture.PublishOfferAsync(
            productId, subscriptionSupplier, currency, supplierCostMinor: 0, priority: 5,
            fulfilmentType: FulfilmentType.DigitalDownload,
            pricingModel: PricingModel.Subscription, billingPeriod: BillingPeriod.Monthly);

        var order = await CheckoutAsync(store, productId, quantity: 1, subscriber: "sg4-renews@example.com");
        await PayAsync(order);

        var line = await OrderLineAsync(order.OrderId);
        Assert.Equal(subscriptionSupplier, line.SupplierId);
        Assert.Equal(BillingMode.Recurring, line.BillingMode);

        Guid subscriptionId = Guid.Empty;
        await WaitAsync(async () =>
        {
            using var scope = fixture.Payments.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var subscription = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.OrderId == order.OrderId);
            subscriptionId = subscription?.Id ?? Guid.Empty;
            return subscription is not null;
        }, $"Subscription for order {order.OrderId} was never set up");

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var subscription = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == subscriptionId);
            Assert.Equal(2_000, subscription.PriceMinor);
            Assert.Equal(currency, subscription.Currency);
            Assert.Equal(store, subscription.StorefrontId);

            var renewed = await scope.ServiceProvider.GetRequiredService<SubscriptionService>()
                .RenewAsync(subscription.TenantId, subscription.Id, default);
            Assert.NotNull(renewed);
            Assert.Equal(2, renewed!.Renewals.Max(r => r.Sequence));
        }

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var entry = await db.JournalEntries.AsNoTracking().Include(e => e.Lines)
                .SingleAsync(e => e.Reference == $"renew-{subscriptionId}-2");
            Assert.All(entry.Lines, l => Assert.Equal(currency, l.Currency));
            Assert.Equal(entry.Lines.Sum(l => l.DebitMinor), entry.Lines.Sum(l => l.CreditMinor));
        }

        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    private async Task<Guid> LiveStorefrontAsync(string currency)
    {
        var storefrontId = Guid.CreateVersion7();
        await fixture.PublishAsync(new StorefrontConfigChanged(
            storefrontId, TenantId, $"Supply Store {currency}", currency, 0, IsLive: true, TaxInclusive: false));
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.StorefrontTaxCopies.AsNoTracking().AnyAsync(c => c.StorefrontId == storefrontId && c.IsLive);
        }, $"Storefront {storefrontId} did not project");
        return storefrontId;
    }

    /// <summary>Fills a fresh cart on <paramref name="storefrontId"/> and checks out. A subscriber is a verified
    /// member with a stored instrument — the only shopper allowed to buy a recurring line.</summary>
    private async Task<CheckoutResponseDto> CheckoutAsync(Guid storefrontId, Guid productId, int quantity, string? subscriber = null)
    {
        using var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
        if (subscriber is not null)
        {
            client.DefaultRequestHeaders.Add(
                InternalClaimsAuth.HeaderName,
                fixture.MintInternalClaims(Guid.NewGuid(), "customer", subscriber, emailVerified: true));
        }

        (await client.PostAsJsonAsync("/cart/items", new { productId, quantity })).EnsureSuccessStatusCode();
        var response = await client.PostAsJsonAsync("/checkout", new
        {
            email = subscriber ?? "supply@example.com",
            shippingAddress = new { name = "S", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
            savedPaymentMethodId = subscriber is null ? (Guid?)null : Guid.NewGuid(),
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
    }

    /// <summary>Pays the order once its saga exists (or the payment would land on nothing) and waits for the order.</summary>
    private async Task PayAsync(CheckoutResponseDto order)
    {
        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.CheckoutStates.AsNoTracking().AnyAsync(x => x.CorrelationId == order.OrderId);
        }, $"Checkout saga for {order.OrderId} did not start");

        using var payments = fixture.Payments.CreateClient();
        (await payments.PostAsync($"/dev/simulate-payment/pi_fake_{order.OrderId:N}?amountMinor={order.GrossMinor}", null))
            .EnsureSuccessStatusCode();

        await WaitAsync(async () =>
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            return await db.Orders.AsNoTracking().AnyAsync(o => o.Id == order.OrderId);
        }, $"Order {order.OrderId} was never confirmed");
    }

    private async Task<ThreeCommerce.Ordering.Domain.CheckoutAttemptLine> AttemptLineAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return await db.CheckoutAttemptLines.AsNoTracking().SingleAsync(l => l.CheckoutAttemptId == orderId);
    }

    private async Task<ThreeCommerce.Ordering.Domain.OrderLine> OrderLineAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return await db.OrderLines.AsNoTracking().SingleAsync(l => l.OrderId == orderId);
    }

    private async Task<IReadOnlyList<ThreeCommerce.BuildingBlocks.Contracts.Ordering.OrderLineInfo>> ConfirmedLinesAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == orderId);
        return OrderConfirmedFactory.From(order).Lines;
    }

    private static async Task<List<JournalLine>> EntryLinesAsync(PaymentsDbContext db, string reference)
    {
        var entry = await db.JournalEntries.AsNoTracking().SingleAsync(e => e.Reference == reference);
        return await db.JournalLines.AsNoTracking().Where(l => l.EntryId == entry.Id).ToListAsync();
    }

    private Task WaitForSupplierPayableAsync(Guid orderId) => WaitAsync(async () =>
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.SupplierPayables.AsNoTracking().AnyAsync(p => p.OrderId == orderId);
    }, $"Supplier payable for order {orderId} was not accrued");

    private Task WaitForEntryAsync(string reference) => WaitAsync(async () =>
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.JournalEntries.AsNoTracking().AnyAsync(e => e.Reference == reference);
    }, $"Journal entry {reference} was not posted");

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

        throw new TimeoutException(what);
    }
}
