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
    public async Task A_zero_gross_order_settles_without_posting_a_journal_entry()
    {
        // A $0 non-shippable order (a usage-metered product with no upfront price — and, shipping nothing,
        // no shipping either) used to 500 the payment: Ledger.Sale posted zero-value debit/credit lines
        // that violate the one-side-nonzero check constraint. It must now settle cleanly with no entry.
        var (productId, _) = await fixture.SeedSuppliedProductAsync(
            priceMinor: 0, supplierCostMinor: 0, fulfilmentType: FulfilmentType.Usage);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        Assert.Equal(0, order.GrossMinor);

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor); // EnsureSuccessStatusCode — 500'd before the fix
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        // A $0 order moves no money → no journal entry at all, and the ledger stays balanced.
        Assert.Equal(0, await CountEntriesForAsync(order.OrderId));
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task Checkout_shipping_honours_the_tenant_product_type_policy()
    {
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        var productId = await fixture.SeedProductAsync(5_000);
        var supplierId = Guid.CreateVersion7();
        await fixture.ApproveSupplierAsync(supplierId); // DECISION A: the offer only counts once approved.

        // A service line: its fulfilment type (ManualService) ships nothing, but its product type is Service.
        await fixture.PublishAsync(new OfferChanged(
            Guid.CreateVersion7(), tenantId, productId, null, supplierId,
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

    [Fact]
    public async Task An_active_in_window_offer_for_the_storefront_is_charged_instead_of_the_catalog_price()
    {
        // offer-as-price (ADR-0028): a product listed at a catalog price of 10000, but with an active,
        // in-window offer scoped to THIS storefront pricing it at 6000, must be CHARGED 6000 at checkout
        // (shown == charged) — not the catalog price — and the sale must still post a balanced entry. A
        // second checkout on a DIFFERENT storefront proves the scope holds: it pays the catalog price.
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        var storefrontId = Guid.CreateVersion7();
        var otherStorefrontId = Guid.CreateVersion7();
        var productId = await fixture.SeedProductAsync(10_000);
        var supplierId = Guid.CreateVersion7();
        await fixture.ApproveSupplierAsync(supplierId); // DECISION A: approve so the offer prices/covers.

        var now = DateTimeOffset.UtcNow;
        await fixture.PublishAsync(new OfferChanged(
            OfferId: Guid.CreateVersion7(), TenantId: tenantId, ProductId: productId, VariantId: null,
            SupplierId: supplierId, SupplyCategory: SupplyCategory.Physical, FulfilmentType: FulfilmentType.Dropship,
            PricingModel: PricingModel.OneTime, BillingPeriod: BillingPeriod.Once, Priority: 0, Active: true,
            SupplierCostMinor: 0, Currency: "EUR", ProductType: ProductType.Physical,
            PriceMinor: 6_000, StorefrontId: storefrontId, ActiveFrom: now.AddHours(-1), ActiveUntil: now.AddHours(1)));

        // Projection test: the new OfferCopy fields (price, storefront scope, window) must project.
        await WaitForOfferPriceCopyAsync(productId, 6_000, storefrontId);

        // On the offer's storefront: charged the OFFER price.
        using (var shopper = fixture.Ordering.CreateClient())
        {
            shopper.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
            await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 2 });
            var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
            Assert.Equal(12_000, order.NetMinor); // 2 × 6000 offer price, not 2 × 10000 catalog

            await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
            await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
            Assert.Equal(0, await fixture.TrialBalanceAsync());
        }

        // On a different storefront the scoped offer does not apply: charged the CATALOG price.
        using (var other = fixture.Ordering.CreateClient())
        {
            other.DefaultRequestHeaders.Add("X-3C-Storefront-Id", otherStorefrontId.ToString());
            await other.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
            var order = (await (await other.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
            Assert.Equal(10_000, order.NetMinor); // catalog price — the offer is scoped to another store
        }
    }

    [Fact]
    public async Task A_line_whose_only_offer_is_from_an_unapproved_supplier_is_blocked_then_buyable_once_approved()
    {
        // DECISION A (strict): a product whose only covering offer is from an UNAPPROVED supplier has no
        // valid supply — checkout rejects it (no price, no availability). Approving the supplier makes it
        // buyable and the sale still posts a balanced entry (trial balance nets 0).
        var (productId, supplierId) = await fixture.SeedSuppliedProductAsync(
            priceMinor: 5_000, supplierCostMinor: 0, fulfilmentType: FulfilmentType.Warehouse, approved: false);

        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        // Unapproved supplier → the line is unavailable → 400 (not booked).
        var blocked = await shopper.PostAsJsonAsync("/checkout", Checkout());
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);

        // Approve the supplier; the same cart now checks out and confirms a balanced sale.
        await fixture.ApproveSupplierAsync(supplierId);
        var allowed = await shopper.PostAsJsonAsync("/checkout", Checkout());
        allowed.EnsureSuccessStatusCode();
        var order = (await allowed.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        Assert.Equal(5_000, order.NetMinor);

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    private async Task WaitForOfferPriceCopyAsync(Guid productId, long priceMinor, Guid storefrontId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>();
            var copy = await db.OfferCopies.AsNoTracking().FirstOrDefaultAsync(o => o.ProductId == productId);
            if (copy is not null && copy.PriceMinor == priceMinor && copy.StorefrontId == storefrontId
                && copy.ActiveFrom is not null && copy.ActiveUntil is not null)
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException($"Offer-price OfferCopy for product {productId} never projected.");
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
    public async Task Checkout_rejects_a_ship_to_country_outside_the_storefront_allowlist()
    {
        var productId = await fixture.SeedProductAsync(9_000);
        var storefrontId = Guid.CreateVersion7();

        // A storefront that ships to AU only. Rate 0 so it never wins tax resolution for other tests'
        // EUR carts; the allowlist is keyed by this unique storefront id so only our client sees it.
        using (var scope = fixture.Ordering.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>();
            db.StorefrontTaxCopies.Add(new ThreeCommerce.Ordering.Domain.StorefrontTaxCopy
            {
                StorefrontId = storefrontId,
                TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Currency = "EUR",
                IsLive = true,
                ShipToCountries = ["AU"],
            });
            await db.SaveChangesAsync();
        }

        using var shopper = fixture.Ordering.CreateClient();
        shopper.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();

        // DE is outside the allowlist → 400 before any payment intent is created.
        var rejected = await shopper.PostAsJsonAsync("/checkout", Checkout()); // country = DE
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("does not ship to DE", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // AU is in the allowlist → checkout proceeds to a payment intent.
        var allowed = await shopper.PostAsJsonAsync("/checkout", new
        {
            email = "buyer@example.com",
            shippingAddress = new { name = "B", line1 = "1 St", city = "Sydney", postcode = "2000", country = "AU" },
        });
        allowed.EnsureSuccessStatusCode();
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
    public async Task Checkout_skips_destination_tax_when_product_rule_exempts_the_country()
    {
        // A live tax regime in a currency no other test uses, so this 10% rate never bleeds into
        // the shared EUR/AUD assertions elsewhere in the collection.
        await SeedLiveTaxAsync("SGD", 1_000);
        var exempt = await SeedProductWithRulesAsync(10_000, "SGD",
            new ThreeCommerce.Ordering.Domain.ProductShipRule("DE", ChargeDestinationTax: false, ShippingCovered: false));
        var taxed = await SeedProductWithRulesAsync(10_000, "SGD"); // no rule → taxed (control)

        // Control: no rule → full 10% destination tax (shipping forced to 0 to isolate goods tax).
        using var control = fixture.Ordering.CreateClient();
        (await control.PostAsJsonAsync("/cart/items", new { productId = taxed, quantity = 1 })).EnsureSuccessStatusCode();
        var controlOrder = (await (await control.PostAsJsonAsync("/checkout", CheckoutWithShipping(0))).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        Assert.Equal(1_000, controlOrder.TaxMinor);
        Assert.Equal(11_000, controlOrder.GrossMinor);

        // Exempt product to DE: destination tax is skipped, but the shopper still pays for the goods.
        using var shopper = fixture.Ordering.CreateClient();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = exempt, quantity = 1 })).EnsureSuccessStatusCode();
        var order = (await (await shopper.PostAsJsonAsync("/checkout", CheckoutWithShipping(0))).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        Assert.Equal(0, order.TaxMinor);
        Assert.Equal(10_000, order.NetMinor);
        Assert.Equal(10_000, order.GrossMinor);

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task Checkout_waives_shipping_when_all_lines_are_shipping_covered()
    {
        // Distinct currency with no tax copy → tax stays 0, isolating the shipping waive.
        var covered = await SeedProductWithRulesAsync(10_000, "SEK",
            new ThreeCommerce.Ordering.Domain.ProductShipRule("DE", ChargeDestinationTax: true, ShippingCovered: true));
        using var shopper = fixture.Ordering.CreateClient();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = covered, quantity = 1 })).EnsureSuccessStatusCode();

        // Default checkout would add FlatShippingMinor (499); the covered rule waives it to 0.
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        Assert.Equal(0, order.ShippingMinor);
        Assert.Equal(10_000, order.NetMinor);
        Assert.Equal(10_000, order.GrossMinor);

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task Checkout_does_not_tax_shipping_when_all_lines_are_destination_tax_exempt()
    {
        // A live 10% regime in a currency no other test uses, so it never bleeds into shared assertions.
        await SeedLiveTaxAsync("PLN", 1_000);
        var exempt = await SeedProductWithRulesAsync(10_000, "PLN",
            new ThreeCommerce.Ordering.Domain.ProductShipRule("DE", ChargeDestinationTax: false, ShippingCovered: false));

        using var shopper = fixture.Ordering.CreateClient();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = exempt, quantity = 1 })).EnsureSuccessStatusCode();

        // Shipping is charged (500) but every line is destination-tax-exempt → tax must be 0. Shipping
        // follows the goods' taxability; taxing it here (regression) would break the exemption.
        var order = (await (await shopper.PostAsJsonAsync("/checkout", CheckoutWithShipping(500))).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        Assert.Equal(0, order.TaxMinor);
        Assert.Equal(10_000, order.NetMinor);
        Assert.Equal(500, order.ShippingMinor);
        Assert.Equal(10_500, order.GrossMinor);

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    private async Task<Guid> SeedProductWithRulesAsync(long priceMinor, string currency, params ThreeCommerce.Ordering.Domain.ProductShipRule[] rules)
    {
        var id = Guid.CreateVersion7();
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>();
        db.ProductCopies.Add(new ThreeCommerce.Ordering.Domain.ProductCopy
        {
            ProductId = id,
            Slug = $"p-{id:N}",
            Title = "Rule Product",
            MinPriceMinor = priceMinor,
            Currency = currency,
            ShipRules = rules.ToList(),
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedLiveTaxAsync(string currency, int basisPoints)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>();
        db.StorefrontTaxCopies.Add(new ThreeCommerce.Ordering.Domain.StorefrontTaxCopy
        {
            StorefrontId = Guid.CreateVersion7(),
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Currency = currency,
            IsLive = true,
            TaxRateBasisPoints = basisPoints,
            TaxInclusive = false,
        });
        await db.SaveChangesAsync();
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
    public async Task A_lost_dispute_charges_the_payment_back_and_records_a_void_payment()
    {
        var productId = await fixture.SeedProductAsync(9_000);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        var intentId = $"pi_fake_{order.OrderId:N}";
        using (var payments = fixture.Payments.CreateClient())
        {
            // A dispute that goes straight to lost: the reversal is booked and the payment is charged back.
            (await payments.PostAsync($"/dev/simulate-chargeback/{intentId}?feeMinor=1500&outcome=lost", null)).EnsureSuccessStatusCode();
        }

        await WaitForEntryAsync($"{order.OrderId}:chargeback");
        Assert.Equal(0, await fixture.TrialBalanceAsync());

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var payment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.Payments.Where(p => p.OrderId == order.OrderId));
            Assert.Equal(ThreeCommerce.Payments.Domain.PaymentStatus.Chargeback, payment.Status);
            Assert.Equal(ThreeCommerce.Payments.Domain.DisputeStatus.Lost, payment.DisputeStatus);

            var voidRecord = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.VoidPayments.Where(v => v.OrderId == order.OrderId));
            Assert.Equal(payment.Id, voidRecord.OriginalPaymentId);
            Assert.Equal(order.GrossMinor, voidRecord.AmountMinor);
            Assert.Equal("dispute_lost", voidRecord.Reason);
        }

        // The terminal PaymentChargedBack flows to Ordering and flags the order disputed.
        await WaitForOrderDisputedAsync(order.OrderId);
    }

    [Fact]
    public async Task A_won_dispute_reinstates_the_funds_and_the_payment_stands_again()
    {
        var productId = await fixture.SeedProductAsync(9_000);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        var intentId = $"pi_fake_{order.OrderId:N}";
        using (var payments = fixture.Payments.CreateClient())
        {
            // Funds withdrawn on dispute, then the merchant wins: the chargeback reversal is itself reversed.
            (await payments.PostAsync($"/dev/simulate-chargeback/{intentId}?feeMinor=1500&outcome=open", null)).EnsureSuccessStatusCode();
            await WaitForEntryAsync($"{order.OrderId}:chargeback");
            (await payments.PostAsync($"/dev/simulate-chargeback/{intentId}?outcome=won", null)).EnsureSuccessStatusCode();
        }

        await WaitForEntryAsync($"{order.OrderId}:chargeback-reinstate");
        Assert.Equal(0, await fixture.TrialBalanceAsync());

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var payment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.Payments.Where(p => p.OrderId == order.OrderId));
            Assert.Equal(ThreeCommerce.Payments.Domain.PaymentStatus.Succeeded, payment.Status);
            Assert.Equal(ThreeCommerce.Payments.Domain.DisputeStatus.Won, payment.DisputeStatus);
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
        // ledger_sf_3: the accrual's supplier-payable credit routes to the store's own account.
        Assert.Equal(12_000, lines.Where(l => l.AccountCode == Accounts.SupplierPayableStoreFor(storefrontId)).Sum(l => l.CreditMinor));
        Assert.Equal(0, lines.Where(l => l.AccountCode == Accounts.LiabilitySupplierPayable).Sum(l => l.CreditMinor)); // shared payable untouched
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
        // ledger_sf_3: supplier-payable credit per storefront.
        Assert.Equal(8_000, lines.Where(l => l.AccountCode == Accounts.SupplierPayableStoreFor(storefrontId)).Sum(l => l.CreditMinor));
        Assert.Equal(0, lines.Where(l => l.AccountCode == Accounts.LiabilitySupplierPayable).Sum(l => l.CreditMinor)); // shared payable untouched
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
        // ledger_sf_3: the restock reversal debits the store's own supplier payable (mirrors the accrual).
        Assert.Equal(12_000, restock.Where(l => l.AccountCode == Accounts.SupplierPayableStoreFor(storefrontId)).Sum(l => l.DebitMinor));
        Assert.Equal(0, restock.Where(l => l.AccountCode == Accounts.LiabilitySupplierPayable).Sum(l => l.DebitMinor)); // shared payable untouched
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

        // The AuthorizePayment consumer persisted the method AND the routed settling provider.
        Guid? storefrontId;
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var payment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.Payments.Where(p => p.OrderId == order.OrderId));
            Assert.Equal(expectedKind, payment.MethodKind);
            Assert.Equal(expectedProvider, payment.Provider);
            storefrontId = payment.StorefrontId;
        }

        // ledger_sf_2: the attributed sale settles into the STORE's own cash for the settling provider.
        var expectedCash = storefrontId is { } sid ? Accounts.CashStoreFor(sid, expectedProvider) : Accounts.CashFor(expectedProvider);

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
    public async Task Card_payment_acquires_through_the_storefronts_default_account_then_falls_back_to_stripe()
    {
        // psp_acquirer_rma / ADR-0042: the acquiring PSP for card / Apple Pay / Google Pay is chosen per
        // storefront. The consumer scopes the default-account lookup to the order's storefront.
        var tenant = new Guid("00000000-0000-0000-0000-000000000001");
        var storefront = Guid.NewGuid();

        // Configure a Polar account as the storefront default acquirer (Draft → submit → activate).
        Guid accountId;
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var now = DateTimeOffset.UtcNow;
            var account = PaymentAccount.Create(tenant, storefront, "Polar acquirer", "polar", PaymentProviderMode.Test, isDefaultForStorefront: true, null, now);
            account.SubmitForApproval(now);
            account.Activate(now);
            db.PaymentAccounts.Add(account);
            await db.SaveChangesAsync();
            accountId = account.Id;
        }

        try
        {
            // A card checkout ON THAT STOREFRONT settles through Polar and posts to cash.polar.
            var routed = await CardCheckoutAsync(6_000, storefront);
            await AssertCardSettledAsync(routed, "polar");

            // Remove the configured default: card checkout reverts to the synthetic stripe acquirer.
            await RemovePaymentAccountAsync(accountId);
            var fallback = await CardCheckoutAsync(6_000, storefront);
            await AssertCardSettledAsync(fallback, "stripe");
        }
        finally
        {
            await RemovePaymentAccountAsync(accountId);
        }
    }

    private async Task<CheckoutResponseDto> CardCheckoutAsync(long priceMinor, Guid? storefrontId = null)
    {
        var productId = await fixture.SeedProductAsync(priceMinor);
        using var shopper = fixture.Ordering.CreateClient();
        if (storefrontId is { } sid)
        {
            shopper.DefaultRequestHeaders.Add("X-3C-Storefront-Id", sid.ToString());
        }

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
        Guid? storefrontId;
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var payment = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                db.Payments.Where(p => p.OrderId == order.OrderId));
            Assert.Equal(PaymentMethodKind.Card, payment.MethodKind);
            Assert.Equal(expectedProvider, payment.Provider);
            storefrontId = payment.StorefrontId;
        }

        // ledger_sf_2: an attributed sale settles into the STORE's own cash for the acquiring provider.
        var expectedCash = storefrontId is { } sid ? Accounts.CashStoreFor(sid, expectedProvider) : Accounts.CashFor(expectedProvider);

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

            // The sale debits the store's cash for the acquiring provider (cash.store-{id}.polar under a
            // Polar default, cash.store-{id}.stripe once no default account is configured).
            Assert.Contains(lines, l => l.AccountCode == expectedCash && l.DebitMinor == order.GrossMinor);
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

    private async Task WaitForOrderDisputedAsync(Guid orderId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>();
            if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                    db.Orders.Where(o => o.Id == orderId && o.Disputed)))
            {
                return;
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"Order {orderId} was not flagged disputed.");
    }
}
