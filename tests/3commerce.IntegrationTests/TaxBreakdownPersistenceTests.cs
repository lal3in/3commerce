using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0063 P1 end to end: checkout asks the tax seam (<see cref="StorefrontRateTaxStrategy"/>) instead of computing
/// tax inline, persists the per-line facts on the checkout attempt, and the confirmed order carries them verbatim —
/// with <c>Σ line tax + shipping tax == TaxMinor</c> on both rows, and the SAME charged amount as before P1 (the
/// expected totals below are the pre-P1 numbers, worked by hand).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class TaxBreakdownPersistenceTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private sealed record CheckoutResponseDto(
        Guid OrderId, string ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
        long GrossMinor, string Currency, string? Message, bool FreeShippingApplied = false,
        List<Guid>? AppliedPromotionIds = null, string? CouponCode = null);

    private sealed record StatusDto(Guid Id, string Status);

    [Fact]
    public async Task An_exclusive_store_with_a_storefront_discount_and_a_ship_rule_exempt_line_persists_facts_that_sum_to_the_tax()
    {
        // 10 % exclusive, 10 % storefront-wide discount. A = 6000 (taxable), B = 4000 (its DE ship rule says
        // ChargeDestinationTax = false). Discount 1000 → 600 on A, 400 on B. Base = (6000 − 600) + 499 shipping
        // = 5899 → 589.9 → 590. Gross = 10000 − 1000 + 499 + 590 = 10089 (unchanged from before P1).
        // Split: A 590 × 5400/5899 = 540.08 → 540; shipping 590 × 499/5899 = 49.91 → 49 + the leftover unit = 50.
        const string currency = "QPA";
        var storefrontId = await SeedStorefrontAsync(currency, taxBps: 1_000, inclusive: false, discountBps: 1_000);
        var taxable = await SeedProductAsync(6_000, currency);
        var exempt = await SeedProductAsync(4_000, currency, new ProductShipRule("DE", ChargeDestinationTax: false, ShippingCovered: false));

        using var shopper = Shopper(storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = taxable, quantity = 1 })).EnsureSuccessStatusCode();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = exempt, quantity = 1 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper);

        Assert.Equal((10_000L, 1_000L, 499L, 590L, 10_089L), (order.NetMinor, order.DiscountMinor, order.ShippingMinor, order.TaxMinor, order.GrossMinor));

        var attempt = await AttemptAsync(order.OrderId);
        AssertFactsSum(attempt.TaxMinor, attempt.Lines.Select(l => l.TaxMinor), attempt.ShippingTaxMinor);
        Assert.Equal((TaxScheme.Domestic, false), (attempt.TaxScheme, attempt.TaxInclusive));
        Assert.Equal((TaxCategory.Standard, 1_000, 499L, 50L, TaxExemptionReason.None),
            (attempt.ShippingTaxCategory, attempt.ShippingTaxRateBps, attempt.ShippingTaxableBaseMinor, attempt.ShippingTaxMinor, attempt.ShippingExemptionReason));
        var a = attempt.Lines.Single(l => l.ProductId == taxable);
        Assert.Equal((TaxCategory.Standard, 1_000, 5_400L, 540L, TaxExemptionReason.None), (a.TaxCategory, a.TaxRateBps, a.TaxableBaseMinor, a.TaxMinor, a.ExemptionReason));
        var b = attempt.Lines.Single(l => l.ProductId == exempt);
        Assert.Equal((TaxCategory.Exempt, 0, 3_600L, 0L, TaxExemptionReason.DestinationShipRule), (b.TaxCategory, b.TaxRateBps, b.TaxableBaseMinor, b.TaxMinor, b.ExemptionReason));

        await SettleAsync(shopper, order);
        await AssertOrderCarriesTheAttemptsFactsAsync(order.OrderId, attempt);
    }

    [Fact]
    public async Task An_inclusive_store_persists_net_bases_whose_tax_sums_to_the_contained_tax()
    {
        // 10 % inclusive. Lines 3333 × 1 and 1111 × 2, shipping 499 → the shopper pays the listed 6054, which
        // contains 6054 × 1000/11000 = 550.36 → 550 (unchanged from before P1). Largest remainder: 302.80 / 201.87 /
        // 45.33 → 302 + 201 + 45 = 548, the two leftover units to .87 then .80 → 303 / 202 / 45.
        const string currency = "QPB";
        var storefrontId = await SeedStorefrontAsync(currency, taxBps: 1_000, inclusive: true, discountBps: 0);
        var first = await SeedProductAsync(3_333, currency);
        var second = await SeedProductAsync(1_111, currency);

        using var shopper = Shopper(storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = first, quantity = 1 })).EnsureSuccessStatusCode();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = second, quantity = 2 })).EnsureSuccessStatusCode();
        var order = await CheckoutAsync(shopper);

        Assert.Equal((5_555L, 0L, 499L, 550L, 6_054L), (order.NetMinor, order.DiscountMinor, order.ShippingMinor, order.TaxMinor, order.GrossMinor));

        var attempt = await AttemptAsync(order.OrderId);
        AssertFactsSum(attempt.TaxMinor, attempt.Lines.Select(l => l.TaxMinor), attempt.ShippingTaxMinor);
        Assert.True(attempt.TaxInclusive);
        var l1 = attempt.Lines.Single(l => l.ProductId == first);
        var l2 = attempt.Lines.Single(l => l.ProductId == second);
        Assert.Equal((3_030L, 303L), (l1.TaxableBaseMinor, l1.TaxMinor));
        Assert.Equal((2_020L, 202L), (l2.TaxableBaseMinor, l2.TaxMinor));
        Assert.Equal((454L, 45L), (attempt.ShippingTaxableBaseMinor, attempt.ShippingTaxMinor));
        // Inclusive: base + tax of every component is exactly what the shopper paid for it.
        Assert.Equal(order.GrossMinor, attempt.Lines.Sum(l => l.TaxableBaseMinor + l.TaxMinor) + attempt.ShippingTaxableBaseMinor + attempt.ShippingTaxMinor);

        await SettleAsync(shopper, order);
        await AssertOrderCarriesTheAttemptsFactsAsync(order.OrderId, attempt);
    }

    private static void AssertFactsSum(long taxMinor, IEnumerable<long> lineTax, long shippingTax) =>
        Assert.Equal(taxMinor, lineTax.Sum() + shippingTax);

    private async Task AssertOrderCarriesTheAttemptsFactsAsync(Guid orderId, CheckoutAttempt attempt)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var placed = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.Id == orderId);

        Assert.Equal(attempt.TaxMinor, placed.TaxMinor);
        AssertFactsSum(placed.TaxMinor, placed.Lines.Select(l => l.TaxMinor), placed.ShippingTaxMinor);
        Assert.Equal(
            (attempt.ShippingTaxCategory, attempt.ShippingTaxRateBps, attempt.ShippingTaxableBaseMinor, attempt.ShippingTaxMinor,
                attempt.ShippingExemptionReason, attempt.TaxScheme, attempt.TaxInclusive),
            (placed.ShippingTaxCategory, placed.ShippingTaxRateBps, placed.ShippingTaxableBaseMinor, placed.ShippingTaxMinor,
                placed.ShippingExemptionReason, placed.TaxScheme, placed.TaxInclusive));
        Assert.Equal(
            attempt.Lines.OrderBy(l => l.ProductId).Select(l => (l.ProductId, l.TaxCategory, l.TaxRateBps, l.TaxableBaseMinor, l.TaxMinor, l.ExemptionReason)),
            placed.Lines.OrderBy(l => l.ProductId).Select(l => (l.ProductId, l.TaxCategory, l.TaxRateBps, l.TaxableBaseMinor, l.TaxMinor, l.ExemptionReason)));
    }

    private async Task<CheckoutAttempt> AttemptAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return await db.CheckoutAttempts.AsNoTracking().Include(a => a.Lines).SingleAsync(a => a.Id == orderId);
    }

    private HttpClient Shopper(Guid storefrontId)
    {
        var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
        return client;
    }

    private static async Task<CheckoutResponseDto> CheckoutAsync(HttpClient shopper)
    {
        var response = await shopper.PostAsJsonAsync("/checkout", new
        {
            email = "tax-facts@example.com",
            shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
    }

    private async Task<Guid> SeedProductAsync(long priceMinor, string currency, params ProductShipRule[] shipRules)
    {
        var id = Guid.CreateVersion7();
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        db.ProductCopies.Add(new ProductCopy
        {
            ProductId = id,
            Slug = $"p-{id:N}",
            Title = "Tax Facts Product",
            MinPriceMinor = priceMinor,
            Currency = currency,
            ShipRules = [.. shipRules],
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedStorefrontAsync(string currency, int taxBps, bool inclusive, int discountBps)
    {
        var storefrontId = Guid.CreateVersion7();
        await fixture.PublishAsync(new StorefrontConfigChanged(
            storefrontId, TenantId, $"Tax Facts {currency}", currency, taxBps, IsLive: true, TaxInclusive: inclusive, DiscountBps: discountBps));
        await Eventually.UntilAsync(
            async () =>
            {
                using var scope = fixture.Ordering.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                var copy = await db.StorefrontTaxCopies.AsNoTracking().FirstOrDefaultAsync(c => c.StorefrontId == storefrontId);
                return copy is { IsLive: true } && copy.TaxRateBasisPoints == taxBps && copy.TaxInclusive == inclusive
                    && copy.DiscountBasisPoints == discountBps;
            },
            () => new TimeoutException($"Storefront copy {storefrontId} did not project."));
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
            interval: TimeSpan.FromMilliseconds(250));

        using var payments = fixture.Payments.CreateClient();
        (await payments.PostAsync($"/dev/simulate-payment/pi_fake_{order.OrderId:N}?amountMinor={order.GrossMinor}", null))
            .EnsureSuccessStatusCode();

        string? last = null;
        await Eventually.UntilAsync(
            async () =>
            {
                last = (await shopper.GetFromJsonAsync<StatusDto>($"/orders/{order.OrderId}/status"))?.Status;
                return last == "Confirmed";
            },
            () => new TimeoutException($"Order {order.OrderId} never confirmed (last status: {last ?? "<none>"})."),
            interval: TimeSpan.FromMilliseconds(250));
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }
}
