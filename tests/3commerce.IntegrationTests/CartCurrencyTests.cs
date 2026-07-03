using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Money-path currency invariants (review remediation rev_1/rev_2): a cart is single-currency,
/// checkout rejects legacy mixed data, and tax is owned by Ordering — Payments charges the
/// tax-inclusive net verbatim (no second tax on top).
/// NB: this collection shares the Ordering/Payments databases with MoneyFlowTests, so the tax
/// test uses AUD — EUR carts elsewhere must keep resolving to zero tax.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class CartCurrencyTests(Phase3Fixture fixture)
{
    private sealed record CheckoutResponseDto(Guid OrderId, string ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor, long GrossMinor, string Currency, string? Message);

    private static object Checkout() => new
    {
        email = "buyer@example.com",
        shippingAddress = new { name = "B", line1 = "1 St", city = "Melbourne", postcode = "3000", country = "AU" },
    };

    [Fact]
    public async Task Adding_a_second_currency_to_a_cart_is_rejected()
    {
        var audProduct = await fixture.SeedProductAsync(2_000, "AUD");
        var eurProduct = await fixture.SeedProductAsync(1_000, "EUR");
        using var shopper = fixture.Ordering.CreateClient();

        var first = await shopper.PostAsJsonAsync("/cart/items", new { productId = audProduct, quantity = 1 });
        first.EnsureSuccessStatusCode();

        var second = await shopper.PostAsJsonAsync("/cart/items", new { productId = eurProduct, quantity = 1 });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // Same currency still adds fine.
        var again = await shopper.PostAsJsonAsync("/cart/items", new { productId = audProduct, quantity = 1 });
        again.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Checkout_rejects_a_legacy_mixed_currency_cart()
    {
        var eurProduct = await fixture.SeedProductAsync(1_000, "EUR");
        using var shopper = fixture.Ordering.CreateClient();
        var add = await shopper.PostAsJsonAsync("/cart/items", new { productId = eurProduct, quantity = 1 });
        add.EnsureSuccessStatusCode();

        // Simulate pre-guard data: force a second line in another currency directly into the cart.
        using (var scope = fixture.Ordering.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var line = await db.CartItems.SingleAsync(i => i.ProductId == eurProduct);
            db.CartItems.Add(new CartItem
            {
                Id = Guid.CreateVersion7(),
                CartId = line.CartId,
                ProductId = Guid.CreateVersion7(),
                Slug = "rogue",
                Title = "Rogue AUD line",
                UnitPriceMinor = 5_000,
                Currency = "AUD",
                Quantity = 1,
            });
            await db.SaveChangesAsync();
        }

        var checkout = await shopper.PostAsJsonAsync("/checkout", Checkout());
        Assert.Equal(HttpStatusCode.BadRequest, checkout.StatusCode);
    }

    private async Task SeedTaxCopyAsync(Guid storefrontId, string currency, int bps, bool inclusive)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var copy = await db.StorefrontTaxCopies.FindAsync(storefrontId);
        if (copy is null)
        {
            db.StorefrontTaxCopies.Add(new StorefrontTaxCopy
            {
                StorefrontId = storefrontId,
                TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Currency = currency,
                TaxRateBasisPoints = bps,
                IsLive = true,
                TaxInclusive = inclusive,
            });
        }
        else
        {
            copy.TaxRateBasisPoints = bps;
            copy.IsLive = true;
            copy.TaxInclusive = inclusive;
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Inclusive_regime_charges_the_listed_price_and_reports_the_contained_tax()
    {
        // AU GST is tax-INCLUSIVE (ADR-0038): the shopper pays exactly the listed amounts.
        await SeedTaxCopyAsync(AudStorefrontId, "AUD", 1_000, inclusive: true);

        var productId = await fixture.SeedProductAsync(10_000, "AUD");
        using var shopper = fixture.Ordering.CreateClient();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();

        var checkout = await shopper.PostAsJsonAsync("/checkout", Checkout());
        checkout.EnsureSuccessStatusCode();
        var order = (await checkout.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        Assert.Equal("AUD", order.Currency);
        Assert.Equal(10_000, order.NetMinor);
        Assert.Equal(499, order.ShippingMinor);
        // Contained GST: round((10000+499) × 1000 / 11000) = 954 — informational, NOT added.
        Assert.Equal(954, order.TaxMinor);
        // The shopper pays exactly goods + shipping; Payments charges it verbatim (rev_2).
        Assert.Equal(order.NetMinor + order.ShippingMinor, order.GrossMinor);
    }

    [Fact]
    public async Task Exclusive_regime_adds_the_tax_on_top()
    {
        // US sales tax is exclusive (ADR-0038): added on goods + shipping at checkout.
        await SeedTaxCopyAsync(UsdStorefrontId, "USD", 825, inclusive: false);

        var productId = await fixture.SeedProductAsync(10_000, "USD");
        using var shopper = fixture.Ordering.CreateClient();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();

        var checkout = await shopper.PostAsJsonAsync("/checkout", Checkout());
        checkout.EnsureSuccessStatusCode();
        var order = (await checkout.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        // Added tax: round((10000+499) × 8.25%) = 866; gross = base + tax.
        Assert.Equal(866, order.TaxMinor);
        Assert.Equal(order.NetMinor + order.ShippingMinor + order.TaxMinor, order.GrossMinor);
    }

    [Fact]
    public async Task Checkout_attributes_the_order_to_the_storefront_from_headers()
    {
        var storefrontId = Guid.CreateVersion7();
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var productId = await fixture.SeedProductAsync(1_000, "EUR");

        using var shopper = fixture.Ordering.CreateClient();
        shopper.DefaultRequestHeaders.Add("X-3C-Tenant-Id", tenantId.ToString());
        shopper.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();

        var checkout = await shopper.PostAsJsonAsync("/checkout", Checkout());
        checkout.EnsureSuccessStatusCode();
        var order = (await checkout.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var attempt = await db.CheckoutAttempts.SingleAsync(a => a.Id == order.OrderId);
        Assert.Equal(storefrontId, attempt.StorefrontId);
        Assert.Equal(tenantId, attempt.TenantId);
    }

    private static readonly Guid AudStorefrontId = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1");
    private static readonly Guid UsdStorefrontId = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000b2");
}
