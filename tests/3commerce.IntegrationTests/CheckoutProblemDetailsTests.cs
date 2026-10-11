using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Every Ordering cart/checkout refusal is RFC 9457 <c>application/problem+json</c> (AGENTS.md "Error
/// Handling"): a stable title, the shopper-facing sentence as <c>detail</c> (the same words the plain-string
/// 400s used to carry), and a numeric code — <c>checkoutBlock</c> for checkout (ADR-0059's pattern, extended
/// additively), <c>cartRefusal</c> for cart mutations. Refusals with a gate-specific setup are pinned in their
/// own suites (ship-to: MoneyFlowTests; storefront not open: StorefrontTaxScopingTests; subscription gates:
/// RecurringCheckoutGateTests; collect: WarehouseCollectAndDeliverTests; coupon limits: CouponRedemptionTests).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class CheckoutProblemDetailsTests(Phase3Fixture fixture)
{
    private static object Checkout() => new
    {
        email = "buyer@example.com",
        shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
    };

    private static object CheckoutWith(
        long? selectedShippingAmountMinor = null, string? selectedShippingService = null,
        DateTimeOffset? selectedShippingExpiresAt = null, string? couponCode = null) => new
        {
            email = "buyer@example.com",
            shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
            selectedShippingAmountMinor,
            selectedShippingService,
            selectedShippingExpiresAt,
            couponCode,
        };

    private async Task<HttpClient> ShopperWithCartAsync(long priceMinor = 2_000)
    {
        var productId = await fixture.SeedProductAsync(priceMinor);
        var shopper = fixture.Ordering.CreateClient();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        return shopper;
    }

    [Fact]
    public async Task An_empty_cart_is_a_problem()
    {
        using var shopper = fixture.Ordering.CreateClient();
        var response = await shopper.PostAsJsonAsync("/checkout", Checkout());
        await ProblemAssert.CheckoutRefusedAsync(response, HttpStatusCode.BadRequest, 3, "Cart is empty."); // CartEmpty
    }

    [Fact]
    public async Task A_checkout_without_a_real_storefront_is_a_problem()
    {
        using var shopper = await ShopperWithCartAsync();
        // The gateway's synthetic DefaultStorefrontId — no real store context (rev_5).
        shopper.DefaultRequestHeaders.Add("X-3C-Storefront-Id", "00000000-0000-0000-0000-000000000101");

        var response = await shopper.PostAsJsonAsync("/checkout", Checkout());
        var body = await ProblemAssert.CheckoutRefusedAsync(
            response, HttpStatusCode.BadRequest, 4, // StorefrontRequired
            "A storefront is required to check out — no store context was resolved.");
        Assert.Equal("A storefront is required to check out.", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_negative_shipping_amount_is_a_problem()
    {
        using var shopper = await ShopperWithCartAsync();
        var response = await shopper.PostAsJsonAsync("/checkout", CheckoutWith(
            selectedShippingAmountMinor: -1, selectedShippingService: "standard",
            selectedShippingExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10)));
        await ProblemAssert.CheckoutRefusedAsync(
            response, HttpStatusCode.BadRequest, 8, "Selected shipping amount cannot be negative."); // InvalidShippingSelection
    }

    [Fact]
    public async Task A_shipping_amount_without_service_and_expiry_is_a_problem()
    {
        using var shopper = await ShopperWithCartAsync();
        var response = await shopper.PostAsJsonAsync("/checkout", CheckoutWith(selectedShippingAmountMinor: 700));
        await ProblemAssert.CheckoutRefusedAsync(
            response, HttpStatusCode.BadRequest, 8, "Selected shipping requires a service and expiry.");
    }

    [Fact]
    public async Task An_expired_shipping_quote_is_a_problem()
    {
        using var shopper = await ShopperWithCartAsync();
        var response = await shopper.PostAsJsonAsync("/checkout", CheckoutWith(
            selectedShippingAmountMinor: 700, selectedShippingService: "standard",
            selectedShippingExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)));
        await ProblemAssert.CheckoutRefusedAsync(
            response, HttpStatusCode.BadRequest, 9, // ShippingQuoteExpired
            "Selected shipping quote has expired; refresh shipping options.");
    }

    [Fact]
    public async Task An_unknown_coupon_is_a_problem_carrying_its_coupon_status()
    {
        using var shopper = await ShopperWithCartAsync();
        var response = await shopper.PostAsJsonAsync("/checkout", CheckoutWith(couponCode: "NO-SUCH-CODE"));
        var body = await ProblemAssert.CheckoutRefusedAsync(
            response, HttpStatusCode.BadRequest, 10, // CouponRefused
            "Coupon code 'NO-SUCH-CODE' is not recognised.");
        Assert.Equal(2, body.GetProperty("couponStatus").GetInt32()); // CouponStatus.UnknownCode, as a number
        Assert.Equal("NO-SUCH-CODE", body.GetProperty("couponCode").GetString());
    }

    [Fact]
    public async Task A_catalog_price_drift_is_a_409_problem_with_the_repriced_totals()
    {
        var productId = await fixture.SeedProductAsync(2_000);
        using var shopper = fixture.Ordering.CreateClient();
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();

        // The catalog price moves after the shopper added the line (stands in for a ProductUpserted).
        using (var scope = fixture.Ordering.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var copy = await db.ProductCopies.SingleAsync(p => p.ProductId == productId);
            copy.MinPriceMinor = 2_500;
            await db.SaveChangesAsync();
        }

        var drift = await shopper.PostAsJsonAsync("/checkout", Checkout());
        var body = await ProblemAssert.CheckoutRefusedAsync(
            drift, HttpStatusCode.Conflict, 15, "Prices changed; review your cart."); // PricesChanged
        // The legacy 409 body's fields survive as extensions: the re-priced totals + "message".
        Assert.Equal(2_500, body.GetProperty("netMinor").GetInt64());
        Assert.Equal("EUR", body.GetProperty("currency").GetString());
        Assert.Equal("Prices changed; review your cart.", body.GetProperty("message").GetString());
        Assert.True(body.GetProperty("grossMinor").GetInt64() >= 2_500);

        // The cart was re-priced, so the shopper's second attempt goes through at the new price.
        var retry = await shopper.PostAsJsonAsync("/checkout", Checkout());
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task Adding_an_unknown_product_is_a_problem()
    {
        using var shopper = fixture.Ordering.CreateClient();
        var response = await shopper.PostAsJsonAsync("/cart/items", new { productId = Guid.NewGuid(), quantity = 1 });
        await ProblemAssert.CartRefusedAsync(response, HttpStatusCode.NotFound, 1, "Unknown product."); // UnknownProduct
    }

    [Fact]
    public async Task Adding_an_unknown_variant_is_a_problem()
    {
        var productId = await fixture.SeedProductAsync(1_000);
        using var shopper = fixture.Ordering.CreateClient();
        var response = await shopper.PostAsJsonAsync("/cart/items", new { productId, variantId = Guid.NewGuid(), quantity = 1 });
        await ProblemAssert.CartRefusedAsync(response, HttpStatusCode.NotFound, 2, "Unknown variant."); // UnknownVariant
    }

    [Fact]
    public async Task Adding_a_product_in_a_currency_it_is_not_priced_in_is_a_problem()
    {
        var productId = await fixture.SeedProductAsync(1_000, "EUR");
        using var shopper = fixture.Ordering.CreateClient();
        var response = await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1, currency = "JPY" });
        await ProblemAssert.CartRefusedAsync(
            response, HttpStatusCode.NotFound, 3, "This product is not available in JPY."); // NotPricedInCurrency
    }

    [Fact]
    public async Task Updating_a_line_that_is_not_in_the_cart_is_a_problem()
    {
        using var shopper = fixture.Ordering.CreateClient();
        var response = await shopper.PutAsJsonAsync($"/cart/items/{Guid.NewGuid()}", new { quantity = 2 });
        await ProblemAssert.CartRefusedAsync(
            response, HttpStatusCode.NotFound, 5, "This item is not in the cart."); // LineNotFound
    }
}
