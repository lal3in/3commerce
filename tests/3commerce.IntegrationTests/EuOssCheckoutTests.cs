using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
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

    private HttpClient Shopper(Guid tenantId, Guid storefrontId)
    {
        var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add("X-3C-Tenant-Id", tenantId.ToString());
        client.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
        return client;
    }

    private static async Task<CheckoutResponseDto> CheckoutAsync(HttpClient shopper, string country)
    {
        var response = await shopper.PostAsJsonAsync("/checkout", new
        {
            email = "eu-buyer@example.com",
            shippingAddress = new { name = "B", line1 = "1 Rue", city = "Somewhere", postcode = "75001", country },
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
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var copy = await db.StorefrontTaxCopies.AsNoTracking().FirstOrDefaultAsync(c => c.StorefrontId == storefrontId);
            if (copy is { IsLive: true } && copy.TaxJurisdiction == jurisdiction && copy.TaxRateBasisPoints == taxBps)
            {
                return storefrontId;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Storefront copy {storefrontId} never projected.");
    }

    private async Task SettleAsync(HttpClient shopper, CheckoutResponseDto order)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            if (await db.CheckoutStates.AsNoTracking().AnyAsync(s => s.CorrelationId == order.OrderId))
            {
                break;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Checkout saga for {order.OrderId} never started.");
            }

            await Task.Delay(250);
        }

        using var payments = fixture.Payments.CreateClient();
        (await payments.PostAsync($"/dev/simulate-payment/pi_fake_{order.OrderId:N}?amountMinor={order.GrossMinor}", null))
            .EnsureSuccessStatusCode();

        var statusDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < statusDeadline)
        {
            var status = await shopper.GetFromJsonAsync<StatusDto>($"/orders/{order.OrderId}/status");
            if (status?.Status == "Confirmed")
            {
                Assert.Equal(0, await fixture.TrialBalanceAsync());
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Order {order.OrderId} never reached Confirmed.");
    }
}
