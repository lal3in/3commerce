using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0063 P1b end to end: a storefront whose tax jurisdiction is an EU Member State checks out through the EU
/// OSS-aware strategy. Below the seller's EUR 10,000 threshold a cross-border sale is taxed at the HOME rate and
/// counted (an <see cref="EuDistanceSale"/> row written with the confirmed order); over it — or by the sale that
/// takes the year over — the DESTINATION rate applies under OSS. Inclusive stores charge the shelf price unchanged;
/// domestic sales and stores without an EU jurisdiction count nothing. Each test runs as its own tenant (the
/// seller), so the running totals never leak between tests.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class EuOssCheckoutTests(Phase3Fixture fixture)
{
    private sealed record CheckoutResponseDto(Guid OrderId, long NetMinor, long ShippingMinor, long TaxMinor, long GrossMinor, string Currency);

    private sealed record StatusDto(Guid Id, string Status);

    [Fact]
    public async Task Below_the_threshold_a_German_store_charges_German_VAT_on_a_French_sale_and_counts_it()
    {
        var tenantId = Guid.CreateVersion7();
        var storefrontId = await SeedStorefrontAsync(tenantId, "DE", taxBps: 1_900, inclusive: true);
        var productId = await fixture.SeedProductAsync(11_900, "EUR");

        using var shopper = Shopper(tenantId, storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper, "FR");

        // Inclusive: the shopper pays the shelf 11900 + 499 shipping; German VAT is contained in it.
        Assert.Equal(12_399, order.GrossMinor);
        Assert.Equal(StorefrontRateTaxStrategy.InclusiveTaxOn(12_399, 1_900), order.TaxMinor); // 1980
        await SettleAsync(shopper, order);

        var (persisted, sale) = await LoadAsync(order.OrderId);
        Assert.Equal((TaxScheme.Domestic, "DE", "FR"), (persisted.TaxScheme, persisted.TaxJurisdiction, persisted.TaxDestinationCountry));
        Assert.NotNull(sale);
        Assert.Equal((tenantId, "DE", "FR", "EUR", TaxScheme.Domestic), (sale.TenantId, sale.HomeCountry, sale.DestinationCountry, sale.Currency, sale.TaxScheme));
        Assert.Equal(12_399 - 1_980, sale.NetMinor); // net of VAT
        Assert.Equal(DateTimeOffset.UtcNow.Year, sale.Year);
    }

    [Fact]
    public async Task Over_the_threshold_the_same_sale_takes_French_VAT_under_OSS_and_the_gross_does_not_move()
    {
        var tenantId = Guid.CreateVersion7();
        await SeedTurnoverAsync(tenantId, DateTimeOffset.UtcNow.Year - 1, EuOssTaxStrategy.ThresholdMinorEur + 1);
        var storefrontId = await SeedStorefrontAsync(tenantId, "DE", taxBps: 1_900, inclusive: true);
        var productId = await fixture.SeedProductAsync(11_900, "EUR");

        using var shopper = Shopper(tenantId, storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper, "FR");

        // The same 12399 is charged; the VAT inside it is now France's 20 %: 12399 × 2000 / 12000 = 2066.5 → 2067.
        Assert.Equal(12_399, order.GrossMinor);
        Assert.Equal(2_067, order.TaxMinor);
        await SettleAsync(shopper, order);

        var (persisted, sale) = await LoadAsync(order.OrderId);
        Assert.Equal(TaxScheme.Oss, persisted.TaxScheme);
        Assert.All(persisted.Lines, l => Assert.Equal(2_000, l.TaxRateBps));
        Assert.Equal(2_000, persisted.ShippingTaxRateBps);
        Assert.Equal(persisted.TaxMinor, persisted.Lines.Sum(l => l.TaxMinor) + persisted.ShippingTaxMinor);
        Assert.Equal(TaxScheme.Oss, sale?.TaxScheme);
    }

    [Fact]
    public async Task The_sale_that_takes_the_current_year_over_the_threshold_is_itself_taxed_at_the_destination()
    {
        // Exclusive German store at 19 %, current-year total 5000 short of the threshold; a 10000 sale crosses it.
        var tenantId = Guid.CreateVersion7();
        await SeedTurnoverAsync(tenantId, DateTimeOffset.UtcNow.Year, EuOssTaxStrategy.ThresholdMinorEur - 5_000);
        var storefrontId = await SeedStorefrontAsync(tenantId, "DE", taxBps: 1_900, inclusive: false);
        var productId = await fixture.SeedProductAsync(10_000, "EUR");

        using var shopper = Shopper(tenantId, storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper, "IT");

        // Italy's 22 % ADDED on goods + shipping: 10499 × 0.22 = 2309.78 → 2310.
        Assert.Equal(2_310, order.TaxMinor);
        Assert.Equal(10_499 + 2_310, order.GrossMinor);
        await SettleAsync(shopper, order);
        Assert.Equal(TaxScheme.Oss, (await LoadAsync(order.OrderId)).Order.TaxScheme);
    }

    [Fact]
    public async Task Domestic_sales_and_stores_without_an_EU_jurisdiction_count_nothing_and_charge_as_before()
    {
        var tenantId = Guid.CreateVersion7();
        // Even far over the threshold a domestic sale is home-taxed; the turnover is irrelevant to it.
        await SeedTurnoverAsync(tenantId, DateTimeOffset.UtcNow.Year - 1, EuOssTaxStrategy.ThresholdMinorEur * 5);
        var german = await SeedStorefrontAsync(tenantId, "DE", taxBps: 1_900, inclusive: true);
        var unconfigured = await SeedStorefrontAsync(tenantId, "", taxBps: 1_900, inclusive: true);
        var productId = await fixture.SeedProductAsync(11_900, "EUR");

        using var domesticShopper = Shopper(tenantId, german);
        (await domesticShopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var domestic = await CheckoutAsync(domesticShopper, "DE");
        Assert.Equal((12_399L, 1_980L), (domestic.GrossMinor, domestic.TaxMinor));
        await SettleAsync(domesticShopper, domestic);

        // A store with no jurisdiction keeps its single rate for a French ship-to (P1 behaviour), OSS or not.
        using var plainShopper = Shopper(tenantId, unconfigured);
        (await plainShopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var plain = await CheckoutAsync(plainShopper, "FR");
        Assert.Equal((12_399L, 1_980L), (plain.GrossMinor, plain.TaxMinor));
        await SettleAsync(plainShopper, plain);

        var (domesticOrder, domesticSale) = await LoadAsync(domestic.OrderId);
        var (plainOrder, plainSale) = await LoadAsync(plain.OrderId);
        Assert.Equal((TaxScheme.Domestic, "DE", "DE"), (domesticOrder.TaxScheme, domesticOrder.TaxJurisdiction, domesticOrder.TaxDestinationCountry));
        Assert.Equal((TaxScheme.Domestic, "", "FR"), (plainOrder.TaxScheme, plainOrder.TaxJurisdiction, plainOrder.TaxDestinationCountry));
        Assert.Null(domesticSale);
        Assert.Null(plainSale);
    }

    [Fact]
    public async Task A_mixed_cart_taxes_the_goods_at_the_ship_to_and_the_e_book_at_the_bill_to_and_counts_only_the_e_book()
    {
        // ADR-0063 § 16, per-line destination. A German inclusive store over the threshold; a book shipped to a German
        // address and an e-book billed to a French one. The book (+ shipping) carries German VAT, the e-book French.
        var tenantId = Guid.CreateVersion7();
        await SeedTurnoverAsync(tenantId, DateTimeOffset.UtcNow.Year - 1, EuOssTaxStrategy.ThresholdMinorEur + 1);
        var storefrontId = await SeedStorefrontAsync(tenantId, "DE", taxBps: 1_900, inclusive: true);
        var book = await fixture.SeedProductAsync(11_900, "EUR");
        var ebook = await SeedDigitalProductAsync(tenantId, 12_000);

        using var shopper = Shopper(tenantId, storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = book, quantity = 1 })).EnsureSuccessStatusCode();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = ebook, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper, "DE", billTo: "FR");

        // Inclusive: the shelf prices + 499 shipping are charged unchanged. German VAT in (11900 + 499) = 1980;
        // French VAT in 12000 = 2000.
        Assert.Equal(11_900 + 12_000 + 499, order.GrossMinor);
        Assert.Equal(1_980 + 2_000, order.TaxMinor);
        await SettleAsync(shopper, order);

        var (persisted, _) = await LoadAsync(order.OrderId);
        Assert.Equal((TaxScheme.Oss, "DE"), (persisted.TaxScheme, persisted.TaxDestinationCountry));
        var bookLine = persisted.Lines.Single(l => l.ProductId == book);
        var ebookLine = persisted.Lines.Single(l => l.ProductId == ebook);
        Assert.Equal(("DE", 1_900), (bookLine.TaxDestinationCountry, bookLine.TaxRateBps));
        Assert.Equal(("FR", 2_000, 2_000L), (ebookLine.TaxDestinationCountry, ebookLine.TaxRateBps, ebookLine.TaxMinor));
        Assert.Equal(1_900, persisted.ShippingTaxRateBps);
        Assert.Equal(persisted.TaxMinor, persisted.Lines.Sum(l => l.TaxMinor) + persisted.ShippingTaxMinor);

        // The running total counts only the part supplied into another Member State: the e-book, net of its VAT.
        var sale = Assert.Single(await SalesAsync(order.OrderId));
        Assert.Equal(("FR", 10_000L, TaxScheme.Oss), (sale.DestinationCountry, sale.NetMinor, sale.TaxScheme));
    }

    [Fact]
    public async Task A_cart_split_across_two_other_member_states_records_one_running_total_row_per_destination()
    {
        // Below the threshold: home (German) VAT throughout, but both cross-border parts count, each under its own
        // destination — the book (+ shipping) shipped to Austria, the e-book billed to Italy.
        var tenantId = Guid.CreateVersion7();
        var storefrontId = await SeedStorefrontAsync(tenantId, "DE", taxBps: 1_900, inclusive: false);
        var book = await fixture.SeedProductAsync(10_000, "EUR");
        var ebook = await SeedDigitalProductAsync(tenantId, 5_000);

        using var shopper = Shopper(tenantId, storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = book, quantity = 1 })).EnsureSuccessStatusCode();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = ebook, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper, "AT", billTo: "IT");

        // Exclusive 19 % added on 10000 + 5000 + 499 = 15499 → 2944.81 → 2945: the same charge as before the split.
        Assert.Equal(2_945, order.TaxMinor);
        await SettleAsync(shopper, order);

        var (persisted, _) = await LoadAsync(order.OrderId);
        Assert.Equal((TaxScheme.Domestic, "AT"), (persisted.TaxScheme, persisted.TaxDestinationCountry));
        Assert.Equal(["AT", "IT"], persisted.Lines.OrderBy(l => l.ProductId == ebook).Select(l => l.TaxDestinationCountry));
        Assert.Equal([("AT", 10_000L + 499), ("IT", 5_000L)], (await SalesAsync(order.OrderId)).Select(s => (s.DestinationCountry, s.NetMinor)));
    }

    [Fact]
    public async Task Goods_shipped_to_the_Canary_Islands_are_an_export_zero_rated_and_never_counted()
    {
        // ADR-0063 § 16, special territories: a postcode 35xxx address is in Spain but outside the EU VAT area. A German
        // inclusive store charges the shelf price with no VAT in it; nothing counts toward the threshold.
        var tenantId = Guid.CreateVersion7();
        var storefrontId = await SeedStorefrontAsync(tenantId, "DE", taxBps: 1_900, inclusive: true);
        var productId = await fixture.SeedProductAsync(11_900, "EUR");

        using var shopper = Shopper(tenantId, storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper, "ES", postcode: "35001");

        Assert.Equal((12_399L, 0L), (order.GrossMinor, order.TaxMinor));
        await SettleAsync(shopper, order);

        var (persisted, _) = await LoadAsync(order.OrderId);
        Assert.Equal((TaxScheme.Export, "ES"), (persisted.TaxScheme, persisted.TaxDestinationCountry));
        var line = Assert.Single(persisted.Lines);
        Assert.Equal((TaxCategory.Exempt, TaxExemptionReason.OutsideEuVatArea, 11_900L), (line.TaxCategory, line.ExemptionReason, line.TaxableBaseMinor));
        Assert.Equal((TaxExemptionReason.OutsideEuVatArea, 499L), (persisted.ShippingExemptionReason, persisted.ShippingTaxableBaseMinor));
        Assert.Empty(await SalesAsync(order.OrderId));
    }

    [Fact]
    public async Task Goods_shipped_to_Northern_Ireland_are_an_OSS_distance_sale_at_the_UK_rate_under_XI()
    {
        // A BT postcode is Northern Ireland: inside the EU VAT area for goods (XI). Over the threshold an exclusive
        // German store adds the UK's 20 % on 10000 + 499 shipping = 2099.8 → 2100, and the sale counts under XI.
        var tenantId = Guid.CreateVersion7();
        await SeedTurnoverAsync(tenantId, DateTimeOffset.UtcNow.Year - 1, EuOssTaxStrategy.ThresholdMinorEur + 1);
        var storefrontId = await SeedStorefrontAsync(tenantId, "DE", taxBps: 1_900, inclusive: false);
        var productId = await fixture.SeedProductAsync(10_000, "EUR");

        using var shopper = Shopper(tenantId, storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper, "GB", postcode: "BT1 1AA");

        Assert.Equal((2_100L, 10_499L + 2_100), (order.TaxMinor, order.GrossMinor));
        await SettleAsync(shopper, order);

        var (persisted, _) = await LoadAsync(order.OrderId);
        Assert.Equal((TaxScheme.Oss, "XI"), (persisted.TaxScheme, persisted.TaxDestinationCountry));
        Assert.Equal(("XI", 2_000), (Assert.Single(persisted.Lines).TaxDestinationCountry, persisted.ShippingTaxRateBps));
        var sale = Assert.Single(await SalesAsync(order.OrderId));
        Assert.Equal(("XI", 10_499L), (sale.DestinationCountry, sale.NetMinor));
    }

    /// <summary>A product whose offer (in THIS tenant) is an electronically supplied download — no shipping.</summary>
    private async Task<Guid> SeedDigitalProductAsync(Guid tenantId, long priceMinor)
    {
        var productId = await fixture.SeedProductAsync(priceMinor, "EUR");
        var supplierId = Guid.CreateVersion7();
        await fixture.ApproveSupplierAsync(supplierId);
        await fixture.PublishAsync(new OfferChanged(
            OfferId: Guid.CreateVersion7(),
            TenantId: tenantId,
            ProductId: productId,
            VariantId: null,
            SupplierId: supplierId,
            SupplyCategory: SupplyCategory.Digital,
            FulfilmentType: FulfilmentType.DigitalDownload,
            PricingModel: PricingModel.OneTime,
            BillingPeriod: BillingPeriod.Once,
            Priority: 0,
            Active: true,
            Currency: "EUR",
            ProductType: ProductType.Digital));
        await Eventually.UntilAsync(
            async () =>
            {
                using var scope = fixture.Ordering.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                return await db.OfferCopies.AnyAsync(o => o.ProductId == productId && o.TenantId == tenantId);
            },
            () => new TimeoutException($"Digital OfferCopy for product {productId} did not project."),
            TimeSpan.FromSeconds(20));
        return productId;
    }

    private async Task<List<EuDistanceSale>> SalesAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return await db.EuDistanceSales.AsNoTracking().Where(s => s.OrderId == orderId).OrderBy(s => s.DestinationCountry).ToListAsync();
    }

    private HttpClient Shopper(Guid tenantId, Guid storefrontId)
    {
        var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add("X-3C-Tenant-Id", tenantId.ToString());
        client.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
        return client;
    }

    private static async Task<CheckoutResponseDto> CheckoutAsync(
        HttpClient shopper, string country, string? billTo = null, string postcode = "75001")
    {
        var response = await shopper.PostAsJsonAsync("/checkout", new
        {
            email = "eu-buyer@example.com",
            shippingAddress = new { name = "B", line1 = "1 Rue", city = "Somewhere", postcode, country },
            billingAddress = billTo is null ? null : new { name = "B", line1 = "2 Via", city = "Elsewhere", postcode = "00100", country = billTo },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
    }

    private async Task<(Order Order, EuDistanceSale? Sale)> LoadAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == orderId);
        var sale = await db.EuDistanceSales.AsNoTracking().SingleOrDefaultAsync(s => s.OrderId == orderId);
        return (order, sale);
    }

    /// <summary>A prior sale of the tenant's, recorded as the confirming consumer would have.</summary>
    private async Task SeedTurnoverAsync(Guid tenantId, int year, long netMinorEur)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        db.EuDistanceSales.Add(new EuDistanceSale
        {
            OrderId = Guid.CreateVersion7(),
            TenantId = tenantId,
            Year = year,
            HomeCountry = "DE",
            DestinationCountry = "AT",
            Currency = "EUR",
            NetMinor = netMinorEur,
            TaxScheme = TaxScheme.Domestic,
            RecordedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedStorefrontAsync(Guid tenantId, string jurisdiction, int taxBps, bool inclusive)
    {
        var storefrontId = Guid.CreateVersion7();
        await fixture.PublishAsync(new StorefrontConfigChanged(
            storefrontId, tenantId, $"EU store {jurisdiction}", "EUR", taxBps, IsLive: true, TaxInclusive: inclusive,
            DiscountBps: 0, TaxJurisdiction: jurisdiction));
        await Eventually.UntilAsync(
            async () =>
            {
                using var scope = fixture.Ordering.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                var copy = await db.StorefrontTaxCopies.AsNoTracking().FirstOrDefaultAsync(c => c.StorefrontId == storefrontId);
                return copy is { IsLive: true } && copy.TaxJurisdiction == jurisdiction && copy.TaxRateBasisPoints == taxBps;
            },
            () => new TimeoutException($"Storefront copy {storefrontId} never projected."),
            TimeSpan.FromSeconds(20));
        return storefrontId;
    }

    private async Task SettleAsync(HttpClient shopper, CheckoutResponseDto order)
    {
        await Eventually.UntilAsync(
            async () =>
            {
                using var scope = fixture.Ordering.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                return await db.CheckoutStates.AsNoTracking().AnyAsync(s => s.CorrelationId == order.OrderId);
            },
            () => new TimeoutException($"Checkout saga for {order.OrderId} never started."),
            TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(250));

        using var payments = fixture.Payments.CreateClient();
        (await payments.PostAsync($"/dev/simulate-payment/pi_fake_{order.OrderId:N}?amountMinor={order.GrossMinor}", null))
            .EnsureSuccessStatusCode();

        await Eventually.UntilAsync(
            async () =>
            {
                var status = await shopper.GetFromJsonAsync<StatusDto>($"/orders/{order.OrderId}/status");
                return status?.Status == "Confirmed";
            },
            () => new TimeoutException($"Order {order.OrderId} never reached Confirmed."),
            interval: TimeSpan.FromMilliseconds(250));
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }
}
