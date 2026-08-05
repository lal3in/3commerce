using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// The Phase-3 money pipeline end to end across Ordering + Payments (fake provider):
/// cart → checkout saga → ledger → confirmation, plus refund and webhook idempotency.
/// FR-3/4/5, NFR-1/3.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class MoneyFlowTests(Phase3Fixture fixture)
{
    private sealed record CheckoutResponseDto(Guid OrderId, string ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor, long GrossMinor, string Currency, string? Message);
    private sealed record StatusDto(Guid Id, string Status);

    private static object Checkout() => new
    {
        email = "buyer@example.com",
        shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
    };

    private static object CheckoutWithShipping(long amountMinor) => new
    {
        email = "buyer@example.com",
        shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
        selectedShippingService = "fake-standard",
        selectedShippingAmountMinor = amountMinor,
        selectedShippingExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
    };

    [Fact]
    public async Task Guest_checkout_confirms_and_posts_a_balanced_sale()
    {
        var productId = await fixture.SeedProductAsync(10_000);
        using var shopper = fixture.Ordering.CreateClient();

        // Add to cart (cookie persists the anonymous cart).
        var add = await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 2 });
        add.EnsureSuccessStatusCode();

        var checkout = await shopper.PostAsJsonAsync("/checkout", Checkout());
        checkout.EnsureSuccessStatusCode();
        var order = (await checkout.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        // Net is items-only (shipping is a separate field): net = 2×10000 = 20000; shipping 499.
        // Tax is 0 — Ordering owns tax and no live StorefrontTaxCopy matches this cart's currency
        // (Payments never applies its own tax); gross = 20000 + 499 + 0 = 20499.
        Assert.Equal(20_000, order.NetMinor);
        Assert.Equal(0, order.TaxMinor);
        Assert.Equal(20_499, order.GrossMinor);
        Assert.StartsWith("pi_fake_", order.ClientSecret);

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task A_digital_only_cart_is_not_charged_shipping()
    {
        // A cart whose only line is a non-physical (digital) product ships nothing → shipping must be 0,
        // even though the flat fallback (499) would otherwise apply. Physical carts still get shipping
        // (asserted by Guest_checkout above, which uses an Unassigned product = defaults to shippable).
        var (productId, _) = await fixture.SeedSuppliedProductAsync(
            priceMinor: 3_000, supplierCostMinor: 0, fulfilmentType: FulfilmentType.DigitalDownload);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        Assert.Equal(0, order.ShippingMinor);
        Assert.Equal(3_000, order.NetMinor);
        Assert.Equal(3_000, order.GrossMinor); // net + 0 shipping + 0 tax
    }

    [Fact]
    public async Task Checkout_shipping_honours_the_tenant_product_type_policy()
    {
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        var productId = await fixture.SeedProductAsync(5_000);

        // A service line: its fulfilment type (ManualService) ships nothing, but its product type is Service.
        await fixture.PublishAsync(new OfferChanged(
            Guid.CreateVersion7(), tenantId, productId, null, Guid.CreateVersion7(),
            SupplyCategory.Service, FulfilmentType.ManualService, PricingModel.OneTime, BillingPeriod.Once,
            Priority: 0, Active: true, SupplierCostMinor: 0, Currency: "EUR", ProductType: ProductType.Service));
        await WaitForOfferCopyAsync(productId);

        try
        {
            // Before any policy: the fulfilment-type gate applies → a service line ships nothing.
            using (var beforeShopper = fixture.Ordering.CreateClient())
            {
                await beforeShopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
                var before = (await (await beforeShopper.PostAsJsonAsync("/checkout", CheckoutWithShipping(700))).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
                Assert.Equal(0, before.ShippingMinor);
            }

            // The tenant marks Service as requiring shipping → a service line now ships and is charged.
            await fixture.PublishAsync(new ThreeCommerce.BuildingBlocks.Contracts.Catalog.ProductTypeShippingPolicyChanged(tenantId, "Service"));
            await WaitForPolicyCopyAsync(tenantId, "Service");

            using var afterShopper = fixture.Ordering.CreateClient();
            await afterShopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
            var after = (await (await afterShopper.PostAsJsonAsync("/checkout", CheckoutWithShipping(700))).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
            Assert.Equal(700, after.ShippingMinor);
        }
        finally
        {
            // Restore the collection's baseline (no policy for the default tenant) for sibling tests.
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>();
            await db.ProductTypeShippingPolicyCopies.Where(p => p.TenantId == tenantId).ExecuteDeleteAsync();
        }
    }

    private async Task WaitForOfferCopyAsync(Guid productId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>();
            if (await db.OfferCopies.AnyAsync(o => o.ProductId == productId))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException($"OfferCopy for product {productId} never projected.");
    }

    private async Task WaitForPolicyCopyAsync(Guid tenantId, string expectedCsv)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>();
            var copy = await db.ProductTypeShippingPolicyCopies.FirstOrDefaultAsync(p => p.TenantId == tenantId);
            if (copy is not null && copy.RequiresShippingTypes == expectedCsv)
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException($"ProductTypeShippingPolicyCopy for tenant {tenantId} never projected.");
    }

    [Fact]
    public async Task Taxed_checkout_splits_net_revenue_from_the_tax_liability_on_the_ledger()
    {
        // fin_tax: a live tax rate for the cart's currency makes checkout charge tax on top (exclusive
        // regime). The tax portion must ride AuthorizePayment → Payment.TaxMinor so the sale books NET
        // revenue plus a separate tax-collected liability — not the whole gross as revenue (which left
        // the Financials tax column and the P&L tax liability empty). A distinct currency isolates this
        // live tax copy from every other test.
        const string currency = "SGD";
        var storefrontId = Guid.CreateVersion7();
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        await fixture.PublishAsync(new StorefrontConfigChanged(
            storefrontId, tenantId, "Tax Store", currency, 2_000, IsLive: true, TaxInclusive: false));
        await WaitForLiveTaxCopyAsync(currency, 2_000);

        var productId = await fixture.SeedProductAsync(10_000, currency);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        // 20% exclusive tax was applied on goods + shipping (exact minor depends on the default shipping),
        // so gross carries a non-zero tax portion. Relationships below are asserted rather than magic
        // numbers so the test stays robust to shipping/rounding.
        Assert.True(order.TaxMinor > 0, $"expected tax to be charged, got {order.TaxMinor}");
        // Product revenue = gross − tax − shipping; shipping books to its own income line (income.shipping).
        var expectedShipping = order.ShippingMinor;
        var expectedNet = order.GrossMinor - order.TaxMinor - expectedShipping;

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        // The tax rode through to the Payment (was hard-coded 0 before)...
        var payment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            db.Payments.Where(p => p.OrderId == order.OrderId));
        Assert.Equal(order.TaxMinor, payment.TaxMinor);

        // ...and the sale split NET revenue from the tax liability (not the whole gross as revenue).
        // Default-storefront order → shared revenue.sales / liability.tax_collected accounts.
        var entry = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            db.JournalEntries.Where(e => e.Reference == order.OrderId.ToString()));
        var lines = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.JournalLines.Where(l => l.EntryId == entry.Id));
        Assert.Equal(expectedNet, lines.Where(l => l.AccountCode == Accounts.RevenueSales).Sum(l => l.CreditMinor));
        Assert.Equal(expectedShipping, lines.Where(l => l.AccountCode == Accounts.ShippingIncome).Sum(l => l.CreditMinor));
        Assert.Equal(order.TaxMinor, lines.Where(l => l.AccountCode == Accounts.LiabilityTaxCollected).Sum(l => l.CreditMinor));
        Assert.Equal(lines.Sum(l => l.DebitMinor), lines.Sum(l => l.CreditMinor));
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    private async Task WaitForLiveTaxCopyAsync(string currency, int bps)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>();
            if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                    db.StorefrontTaxCopies.Where(c => c.Currency == currency && c.IsLive && c.TaxRateBasisPoints == bps)))
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Live tax copy for {currency} did not project.");
    }

    [Fact]
    public async Task Checkout_uses_the_selected_shipping_quote_amount()
    {
        var productId = await fixture.SeedProductAsync(10_000);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var order = (await (await shopper.PostAsJsonAsync("/checkout", CheckoutWithShipping(1_234))).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        Assert.Equal(10_000, order.NetMinor);
        Assert.Equal(1_234, order.ShippingMinor);
        Assert.Equal(11_234, order.GrossMinor);
    }

    [Fact]
    public async Task Duplicate_payment_webhook_posts_one_journal_entry()
    {
        var productId = await fixture.SeedProductAsync(5_000);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        var before = await fixture.TrialBalanceAsync();
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor); // same event id → deduped
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        // Exactly one sale entry was posted (trial balance stays zero; entry count is one).
        Assert.Equal(0, await fixture.TrialBalanceAsync());
        Assert.Equal(0, before);
        Assert.Equal(1, await CountEntriesForAsync(order.OrderId));
    }

    [Fact]
    public async Task Refund_reverses_the_sale_and_keeps_the_ledger_balanced()
    {
        var productId = await fixture.SeedProductAsync(8_000);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        // Publish a refund directly (same contract the admin endpoint / Phase-4 RMA use).
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<MassTransit.IPublishEndpoint>();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            await bus.Publish(new ThreeCommerce.BuildingBlocks.Contracts.Payments.RefundRequested(
                Guid.CreateVersion7(), order.OrderId, order.GrossMinor, "test", "admin"));
            await db.SaveChangesAsync();
        }

        await WaitForRefundAsync(order.OrderId);
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task A_chargeback_reverses_the_sale_books_a_dispute_fee_and_marks_the_payment_disputed()
    {
        var productId = await fixture.SeedProductAsync(9_000);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        using (var payments = fixture.Payments.CreateClient())
        {
            var intentId = $"pi_fake_{order.OrderId:N}";
            (await payments.PostAsync($"/dev/simulate-chargeback/{intentId}?feeMinor=1500", null)).EnsureSuccessStatusCode();
        }

        // The chargeback entry has its own "{orderId}:chargeback" reference (distinct from the sale's).
        await WaitForEntryAsync($"{order.OrderId}:chargeback");
        var lines = await EntryLinesAsync($"{order.OrderId}:chargeback");
        Assert.Contains(lines, l => l.AccountCode.EndsWith("_chargeback_fees", StringComparison.Ordinal) && l.DebitMinor == 1500);
        Assert.Equal(0, await fixture.TrialBalanceAsync());

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var status = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.Payments.Where(p => p.OrderId == order.OrderId).Select(p => p.Status));
            Assert.Equal(ThreeCommerce.Payments.Domain.PaymentStatus.Disputed, status);
        }
    }

    [Fact]
    public async Task Admin_cannot_cancel_a_confirmed_order_and_must_refund_instead()
    {
        var productId = await fixture.SeedProductAsync(5_000);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        using var admin = AdminOrderingClient();
        var response = await admin.PostAsJsonAsync($"/admin/orders/{order.OrderId}/cancel", new { reason = "test" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Admin_cancelling_an_unknown_order_is_not_found()
    {
        using var admin = AdminOrderingClient();
        var response = await admin.PostAsJsonAsync($"/admin/orders/{Guid.NewGuid()}/cancel", new { reason = "test" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_paid_order_with_supplier_cost_lines_accrues_per_store_cogs()
    {
        // cogs_wiring: an order whose lines resolve to a supplier (via a projected OfferChanged → OfferCopy,
        // the REAL production path) carrying a per-unit supplier cost must accrue COGS once paid — the
        // previously-dormant SupplierPayable path. The order is default-storefront-attributed (storefrontId
        // = the default tenant), so the COGS debit lands on that store's own expense.cogs.store-{id} account,
        // not the shared fallback. No direct EF writes to OfferCopy/SupplierPayable — the wiring proves itself.
        var storefrontId = new Guid("00000000-0000-0000-0000-000000000001");
        var (productId, supplierId) = await fixture.SeedSuppliedProductAsync(priceMinor: 10_000, supplierCostMinor: 4_000);

        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 3 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
        await WaitForSupplierPayableAsync(order.OrderId);

        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        // One payable for the order's single supplier: gross = 4000 × 3 = 12000; no commission policy → net = gross.
        var payable = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            db.SupplierPayables.Where(p => p.OrderId == order.OrderId));
        Assert.Equal(supplierId, payable.SupplierEntityId);
        Assert.Equal(12_000, payable.GrossMinor);
        Assert.Equal(12_000, payable.NetPayableMinor);

        // The balanced accrual (referenced by the payable id): Dr the store's own COGS / Cr the supplier payable.
        var entry = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            db.JournalEntries.Where(e => e.Reference == payable.Id.ToString()));
        var lines = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.JournalLines.Where(l => l.EntryId == entry.Id));
        Assert.Equal(12_000, lines.Where(l => l.AccountCode == Accounts.CogsStoreFor(storefrontId)).Sum(l => l.DebitMinor));
        Assert.Equal(0, lines.Where(l => l.AccountCode == Accounts.CostOfGoodsSold).Sum(l => l.DebitMinor)); // shared fallback untouched
        Assert.Equal(12_000, lines.Where(l => l.AccountCode == Accounts.LiabilitySupplierPayable).Sum(l => l.CreditMinor));
        Assert.Equal(lines.Sum(l => l.DebitMinor), lines.Sum(l => l.CreditMinor));

        // Reconciliation guard (ADR-0041): every COGS line MUST carry the order's currency. When it was
        // empty (fixed in #157), the Financials per-currency P&L — which filters COGS by line currency —
        // showed 0 while the by-store column (which sums a store's account across all currencies) showed
        // the value: the two views silently disagreed. Assert the line currency, then confirm the
        // per-currency query over the store's COGS account equals the by-store total (they reconcile).
        Assert.All(lines, l => Assert.False(string.IsNullOrEmpty(l.Currency), $"line {l.AccountCode} has no currency"));
        Assert.All(lines, l => Assert.Equal(order.Currency, l.Currency));
        // Scoped to THIS entry (the shared Phase3 fixture DB accumulates other tests' COGS on the same
        // default-store account, so a whole-table sum can't assert an absolute): a currency-filtered view
        // over this accrual's store-COGS lines finds the full amount → per-currency and by-store agree.
        Assert.Equal(12_000, lines.Where(l => l.AccountCode == Accounts.CogsStoreFor(storefrontId) && l.Currency == order.Currency).Sum(l => l.DebitMinor));

        // Sale + COGS accrual both balanced → the whole ledger stays balanced.
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task A_paid_order_relabels_a_foreign_currency_supplier_cost_into_the_order_currency()
    {
        // no_fx relabel: the offer's supplier cost is denominated in EUR but the order settles in AUD. With
        // no FX feed the cost minor amount is carried into the order's currency without conversion — the
        // same deliberate posture as the carrier-cost consumer (a mock-PSP-style relabel), because an
        // AUD store showing revenue with zero COGS (a fake 100% margin) is worse dev data. The COGS still
        // accrues, in AUD, for the same minor amount, and the ledger stays balanced.
        var storefrontId = new Guid("00000000-0000-0000-0000-000000000001");
        var (productId, supplierId) = await fixture.SeedSuppliedProductAsync(
            priceMinor: 10_000, supplierCostMinor: 4_000, currency: "AUD", offerCurrency: "EUR");

        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 2 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
        await WaitForSupplierPayableAsync(order.OrderId);

        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        // Cost = 4000 × 2 = 8000, relabelled from EUR into the order's AUD (same minor amount, no conversion).
        var payable = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            db.SupplierPayables.Where(p => p.OrderId == order.OrderId));
        Assert.Equal(supplierId, payable.SupplierEntityId);
        Assert.Equal(8_000, payable.GrossMinor);
        Assert.Equal("AUD", payable.Currency);

        // Account routing is unchanged by the relabel: still the order's own per-store COGS / supplier payable.
        var entry = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            db.JournalEntries.Where(e => e.Reference == payable.Id.ToString()));
        var lines = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.JournalLines.Where(l => l.EntryId == entry.Id));
        Assert.Equal(8_000, lines.Where(l => l.AccountCode == Accounts.CogsStoreFor(storefrontId)).Sum(l => l.DebitMinor));
        Assert.Equal(8_000, lines.Where(l => l.AccountCode == Accounts.LiabilitySupplierPayable).Sum(l => l.CreditMinor));
        Assert.Equal(lines.Sum(l => l.DebitMinor), lines.Sum(l => l.CreditMinor));
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task Rma_storage_disposition_writes_off_the_cogs_then_an_edit_to_restock_reverses_it()
    {
        // rma_disposition: a paid order with supplier cost accrues per-store COGS; an RMA dispositioned to
        // Storage(Damage) must reclass that COGS to the store's write-off account, and editing the
        // disposition to Restock must reverse the write-off and reverse the accrual instead. Publishing
        // RmaDispositionSet directly stands in for Support (as the fixture does for other services' events)
        // and drives the real Ordering → Payments chain: Ordering values the returned goods from the order
        // lines, Payments corrects the accrual. The ledger stays balanced throughout.
        var storefrontId = new Guid("00000000-0000-0000-0000-000000000001");
        var (productId, _) = await fixture.SeedSuppliedProductAsync(priceMinor: 10_000, supplierCostMinor: 4_000);

        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 3 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
        await WaitForSupplierPayableAsync(order.OrderId);

        var cogs = Accounts.CogsStoreFor(storefrontId);
        var writeoff = Accounts.WriteoffsStoreFor(storefrontId);
        var rmaId = Guid.CreateVersion7();

        // Storage(Damage), revision 1: reclass the whole order's COGS (3 × 4000 = 12000, no commission) to
        // the store's write-off account. RefundedMinor = the order gross → a whole-order return (scale 1).
        await fixture.PublishAsync(new ThreeCommerce.BuildingBlocks.Contracts.Support.RmaDispositionSet(
            rmaId, order.OrderId, Kind: 2, StorageReason: 1, Revision: 1, RefundedMinor: order.GrossMinor));

        await WaitForEntryAsync($"{rmaId}:1");
        var storage = await EntryLinesAsync($"{rmaId}:1");
        Assert.Equal(12_000, storage.Where(l => l.AccountCode == writeoff).Sum(l => l.DebitMinor));
        Assert.Equal(12_000, storage.Where(l => l.AccountCode == cogs).Sum(l => l.CreditMinor));
        Assert.Equal(storage.Sum(l => l.DebitMinor), storage.Sum(l => l.CreditMinor));
        Assert.Equal(0, await fixture.TrialBalanceAsync());

        // Edit to Restock, revision 2: reverse the revision-1 write-off, then reverse the accrual instead
        // (Dr liability.supplier_payable / Cr the store COGS).
        await fixture.PublishAsync(new ThreeCommerce.BuildingBlocks.Contracts.Support.RmaDispositionSet(
            rmaId, order.OrderId, Kind: 1, StorageReason: null, Revision: 2, RefundedMinor: order.GrossMinor));

        await WaitForEntryAsync($"{rmaId}:2");
        await WaitForEntryAsync($"{rmaId}:2:reversal");

        var reversal = await EntryLinesAsync($"{rmaId}:2:reversal");
        // The revision-1 write-off is undone line for line: Dr cogs / Cr writeoff.
        Assert.Equal(12_000, reversal.Where(l => l.AccountCode == cogs).Sum(l => l.DebitMinor));
        Assert.Equal(12_000, reversal.Where(l => l.AccountCode == writeoff).Sum(l => l.CreditMinor));
        Assert.Equal(reversal.Sum(l => l.DebitMinor), reversal.Sum(l => l.CreditMinor));

        var restock = await EntryLinesAsync($"{rmaId}:2");
        Assert.Equal(12_000, restock.Where(l => l.AccountCode == Accounts.LiabilitySupplierPayable).Sum(l => l.DebitMinor));
        Assert.Equal(12_000, restock.Where(l => l.AccountCode == cogs).Sum(l => l.CreditMinor));
        Assert.Equal(restock.Sum(l => l.DebitMinor), restock.Sum(l => l.CreditMinor));

        // The write-off account nets back to zero after the edit (posted then reversed); the ledger balances.
        Assert.Equal(0, await AccountBalanceAsync(writeoff));
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    private async Task WaitForEntryAsync(string reference)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Payments.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                    db.JournalEntries.Where(e => e.Reference == reference)))
            {
                return;
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"Journal entry {reference} was not posted.");
    }

    private async Task<List<ThreeCommerce.Payments.Domain.Ledger.JournalLine>> EntryLinesAsync(string reference)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var entry = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            db.JournalEntries.Where(e => e.Reference == reference));
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.JournalLines.Where(l => l.EntryId == entry.Id));
    }

    private async Task<long> AccountBalanceAsync(string accountCode)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var debits = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SumAsync(
            db.JournalLines.Where(l => l.AccountCode == accountCode), l => l.DebitMinor);
        var credits = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SumAsync(
            db.JournalLines.Where(l => l.AccountCode == accountCode), l => l.CreditMinor);
        return debits - credits;
    }

    private async Task WaitForSupplierPayableAsync(Guid orderId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Payments.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                    db.SupplierPayables.Where(p => p.OrderId == orderId)))
            {
                return;
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"Supplier payable for order {orderId} was not accrued.");
    }

    private System.Net.Http.HttpClient AdminOrderingClient()
    {
        var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));
        return client;
    }

    [Fact]
    public async Task Checkout_saga_survives_an_ordering_outage_during_payment()
    {
        // NFR-2 chaos: the saga host dies after checkout but before the payment lands.
        var productId = await fixture.SeedProductAsync(7_000);
        using (var shopper = fixture.Ordering.CreateClient())
        {
            await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
            var pending = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
            await WaitForSagaAsync(pending.OrderId); // saga durably awaiting payment

            // Outage: Ordering (the saga owner) goes down.
            await fixture.RestartOrderingAsync();

            // Payment succeeds while Ordering is restarting — PaymentSucceeded queues durably.
            using var payments = fixture.Payments.CreateClient();
            var intentId = $"pi_fake_{pending.OrderId:N}";
            (await payments.PostAsync($"/dev/simulate-payment/{intentId}?amountMinor={pending.GrossMinor}", null)).EnsureSuccessStatusCode();

            // The restarted host drains the queue and the saga still reaches Confirmed.
            using var recovered = fixture.Ordering.CreateClient();
            await WaitForStatusAsync(recovered, pending.OrderId, "Confirmed");
            Assert.Equal(0, await fixture.TrialBalanceAsync());
        }
    }

    [Fact]
    public async Task Checkout_emits_one_distributed_trace_across_the_http_and_message_hops()
    {
        // NFR-7: the HTTP-initiated checkout trace must carry through the async message
        // hops (MassTransit propagates context through the outbox), so the same TraceId
        // spans the AspNetCore entry span and the saga/consume spans.
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var productId = await fixture.SeedProductAsync(6_000);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
        await Task.Delay(500); // let the last consume spans flush

        // A single trace must contain both an HTTP server span and a MassTransit span.
        var byTrace = activities
            .Where(a => a.TraceId != default)
            .GroupBy(a => a.TraceId);
        var correlated = byTrace.FirstOrDefault(g =>
            g.Any(a => a.Source.Name.StartsWith("Microsoft.AspNetCore")) &&
            g.Any(a => a.Source.Name == "MassTransit"));

        Assert.True(correlated is not null,
            $"no trace spanned both HTTP and MassTransit; saw sources: " +
            string.Join(", ", activities.Select(a => a.Source.Name).Distinct().OrderBy(s => s)));
    }

    private async Task SimulatePaymentAsync(Guid orderId, long gross)
    {
        // In reality the client confirms payment seconds after checkout; wait for the saga
        // to have started (CartSubmitted delivered via the outbox) so the success isn't dropped.
        await WaitForSagaAsync(orderId);

        using var payments = fixture.Payments.CreateClient();
        var intentId = $"pi_fake_{orderId:N}";
        var r = await payments.PostAsync($"/dev/simulate-payment/{intentId}?amountMinor={gross}", null);
        r.EnsureSuccessStatusCode();
    }

    private async Task WaitForSagaAsync(Guid orderId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>();
            if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                    db.CheckoutStates.Where(s => s.CorrelationId == orderId)))
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

    [Theory]
    [InlineData("GooglePay")]
    [InlineData("ApplePay")]
    [InlineData("PayPal")]
    [InlineData("CreditCard")]
    public async Task The_chosen_payment_method_survives_checkout_and_is_attributed_on_the_ledger(string paymentOption)
    {
        var productId = await fixture.SeedProductAsync(6_000);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var checkout = await shopper.PostAsJsonAsync("/checkout", new
        {
            email = "buyer@example.com",
            shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
            paymentOption,
        });
        checkout.EnsureSuccessStatusCode();
        var order = (await checkout.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        // "CreditCard" is the card default; the wallets/PSP options map to their own kind (ADR-0039).
        var expectedKind = paymentOption == "CreditCard" ? PaymentMethodKind.Card : Enum.Parse<PaymentMethodKind>(paymentOption);

        // Card / Apple Pay / Google Pay are tokenized through the card PSP, so they still settle on the
        // tenant default (stripe → cash.stripe). PayPal is a standalone PSP: it routes to paypal and the
        // sale must post to cash.paypal (ADR-0039 payment-method routing).
        var expectedProvider = paymentOption == "PayPal" ? "paypal" : "stripe";
        var expectedCash = Accounts.CashFor(expectedProvider);

        // The AuthorizePayment consumer persisted the method AND the routed settling provider.
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var payment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.Payments.Where(p => p.OrderId == order.OrderId));
            Assert.Equal(expectedKind, payment.MethodKind);
            Assert.Equal(expectedProvider, payment.Provider);
        }

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var entry = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.JournalEntries.Where(e => e.Reference == order.OrderId.ToString()));
            var lines = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                db.JournalLines.Where(l => l.EntryId == entry.Id));

            // The admin ledger renders Description — the method must be readable there.
            Assert.Contains($"via {expectedKind}", entry.Description);

            // The sale debits the settling provider's cash account: cash.stripe for the card PSP,
            // cash.paypal when PayPal settled it.
            Assert.Contains(lines, l => l.AccountCode == expectedCash && l.DebitMinor == order.GrossMinor);
            Assert.Equal(lines.Sum(l => l.DebitMinor), lines.Sum(l => l.CreditMinor));
        }

        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task Card_payment_acquires_through_the_tenant_default_account_then_falls_back_to_stripe()
    {
        // psp_acquirer_rma: the acquiring PSP for card / Apple Pay / Google Pay is chosen at the
        // tenant/admin level. AuthorizePayment carries no tenant in dev, so the consumer scopes the
        // default-account lookup to Tenancy:DefaultTenantId (unset here → the seeded default tenant).
        var tenant = new Guid("00000000-0000-0000-0000-000000000001");

        // Configure a Polar account as the tenant default acquirer (Draft → submit → activate).
        Guid accountId;
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var now = DateTimeOffset.UtcNow;
            var account = PaymentAccount.Create(tenant, null, "Polar acquirer", "polar", PaymentProviderMode.Test, isDefaultForTenant: true, null, now);
            account.SubmitForApproval(now);
            account.Activate(now);
            db.PaymentAccounts.Add(account);
            await db.SaveChangesAsync();
            accountId = account.Id;
        }

        try
        {
            // A plain card checkout now settles through Polar and posts to cash.polar with Provider=polar.
            var routed = await CardCheckoutAsync(6_000);
            await AssertCardSettledAsync(routed, "polar");

            // Remove the configured default: card checkout reverts to the synthetic stripe acquirer.
            await RemovePaymentAccountAsync(accountId);
            var fallback = await CardCheckoutAsync(6_000);
            await AssertCardSettledAsync(fallback, "stripe");
        }
        finally
        {
            // Never leave a default account behind — the shared Phase-3 DB would then route every other
            // test's card payment through Polar.
            await RemovePaymentAccountAsync(accountId);
        }
    }

    private async Task<CheckoutResponseDto> CardCheckoutAsync(long priceMinor)
    {
        var productId = await fixture.SeedProductAsync(priceMinor);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var checkout = await shopper.PostAsJsonAsync("/checkout", new
        {
            email = "buyer@example.com",
            shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
            paymentOption = "CreditCard",
        });
        checkout.EnsureSuccessStatusCode();
        return (await checkout.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
    }

    private async Task AssertCardSettledAsync(CheckoutResponseDto order, string expectedProvider)
    {
        // The AuthorizePayment consumer persisted the card method AND the acquiring provider.
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var payment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.Payments.Where(p => p.OrderId == order.OrderId));
            Assert.Equal(PaymentMethodKind.Card, payment.MethodKind);
            Assert.Equal(expectedProvider, payment.Provider);
        }

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        using var shopper = fixture.Ordering.CreateClient();
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var entry = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.JournalEntries.Where(e => e.Reference == order.OrderId.ToString()));
            var lines = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                db.JournalLines.Where(l => l.EntryId == entry.Id));

            // The sale debits the acquiring provider's cash account: cash.polar under a Polar default,
            // cash.stripe once no default account is configured.
            Assert.Contains(lines, l => l.AccountCode == Accounts.CashFor(expectedProvider) && l.DebitMinor == order.GrossMinor);
            Assert.Equal(lines.Sum(l => l.DebitMinor), lines.Sum(l => l.CreditMinor));
        }

        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    private async Task RemovePaymentAccountAsync(Guid accountId)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.PaymentAccounts.Where(a => a.Id == accountId));
        if (rows.Count > 0)
        {
            db.PaymentAccounts.RemoveRange(rows);
            await db.SaveChangesAsync();
        }
    }

    private async Task<int> CountEntriesForAsync(Guid orderId)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
            db.JournalEntries.Where(e => e.Reference == orderId.ToString()));
    }

    private async Task WaitForRefundAsync(Guid orderId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Payments.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                    db.Refunds.Where(r => r.OrderId == orderId)))
            {
                return;
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"Refund for order {orderId} was not processed.");
    }
}
