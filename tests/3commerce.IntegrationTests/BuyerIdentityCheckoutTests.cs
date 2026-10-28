using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Tax;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0063 P3 end to end through Ordering's real checkout: a guest and a member each check out with and without a
/// business-purchase block; the bill-to defaults to the ship-to address and a separate one is kept; the block is
/// persisted on the checkout attempt and carried verbatim onto the confirmed order (and back out on the order
/// detail, scheme as a NUMBER); the amount charged is identical with and without it (no reverse charge, § 12 D5);
/// and a malformed block is a 400 problem+json naming the field, with nothing booked.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class BuyerIdentityCheckoutTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private sealed record CheckoutResponseDto(
        Guid OrderId, string ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
        long GrossMinor, string Currency, string? Message, bool FreeShippingApplied = false,
        List<Guid>? AppliedPromotionIds = null, string? CouponCode = null);

    private sealed record StatusDto(Guid Id, string Status);

    private static readonly object ShipTo = new { name = "Ship Name", line1 = "1 Ship St", city = "Berlin", postcode = "10115", country = "DE" };

    [Fact]
    public async Task A_guest_checks_out_with_and_without_a_business_block_and_is_charged_the_same()
    {
        // 10 % exclusive store: tax is non-zero, so "unchanged" is a real comparison, not 0 == 0.
        const string currency = "QBA";
        var storefrontId = await SeedStorefrontAsync(currency);
        var productId = await SeedProductAsync(4_321, currency);

        using var consumer = Shopper(storefrontId);
        (await consumer.PostAsJsonAsync("/cart/items", new { productId, quantity = 2 })).EnsureSuccessStatusCode();
        var plain = await CheckoutAsync(consumer, new { email = "guest-consumer@example.com", shippingAddress = ShipTo });

        using var business = Shopper(storefrontId);
        (await business.PostAsJsonAsync("/cart/items", new { productId, quantity = 2 })).EnsureSuccessStatusCode();
        var withBlock = await CheckoutAsync(business, new
        {
            email = "guest-business@example.com",
            shippingAddress = ShipTo,
            billingAddress = new { name = "Acme Accounts", line1 = "9 Ledger Rd", city = "Munich", postcode = "80331", country = "de", region = "BY" },
            buyer = new { companyName = " Acme GmbH ", taxId = "de 123 456 789", taxIdScheme = (int)TaxIdScheme.EuVat },
        });

        // The charge is byte-identical: a buyer tax ID changes nothing until reverse charge lands (phase 2).
        Assert.Equal(
            (plain.NetMinor, plain.DiscountMinor, plain.ShippingMinor, plain.TaxMinor, plain.GrossMinor),
            (withBlock.NetMinor, withBlock.DiscountMinor, withBlock.ShippingMinor, withBlock.TaxMinor, withBlock.GrossMinor));
        Assert.True(plain.TaxMinor > 0);

        // Consumer: the bill-to IS the ship-to, and there is no business block.
        var consumerAttempt = await AttemptAsync(plain.OrderId);
        Assert.Equal(("Ship Name", "1 Ship St", "Berlin", (string?)null, "10115", "DE"),
            (consumerAttempt.BillName, consumerAttempt.BillLine1, consumerAttempt.BillCity, consumerAttempt.BillRegion, consumerAttempt.BillPostcode, consumerAttempt.BillCountry));
        Assert.Null(consumerAttempt.BuyerCompanyName);
        Assert.Null(consumerAttempt.BuyerTaxIdScheme);

        // Business: the separate bill-to and the normalized block, with the jurisdiction the VAT prefix implies.
        var businessAttempt = await AttemptAsync(withBlock.OrderId);
        Assert.Null(businessAttempt.UserId);
        Assert.Equal(("Acme Accounts", "9 Ledger Rd", "Munich", (string?)"BY", "80331", "DE"),
            (businessAttempt.BillName, businessAttempt.BillLine1, businessAttempt.BillCity, businessAttempt.BillRegion, businessAttempt.BillPostcode, businessAttempt.BillCountry));
        Assert.Equal(("Acme GmbH", "DE123456789", (TaxIdScheme?)TaxIdScheme.EuVat, "DE"),
            (businessAttempt.BuyerCompanyName, businessAttempt.BuyerTaxId, businessAttempt.BuyerTaxIdScheme, businessAttempt.BuyerTaxIdJurisdiction));

        await SettleAsync(business, withBlock);
        var order = await OrderAsync(withBlock.OrderId);
        Assert.Equal(
            (businessAttempt.BillName, businessAttempt.BillLine1, businessAttempt.BillCity, businessAttempt.BillRegion, businessAttempt.BillPostcode, businessAttempt.BillCountry,
                businessAttempt.BuyerCompanyName, businessAttempt.BuyerTaxId, businessAttempt.BuyerTaxIdScheme, businessAttempt.BuyerTaxIdJurisdiction),
            (order.BillName, order.BillLine1, order.BillCity, order.BillRegion, order.BillPostcode, order.BillCountry,
                order.BuyerCompanyName, order.BuyerTaxId, order.BuyerTaxIdScheme, order.BuyerTaxIdJurisdiction));
        Assert.Equal(withBlock.GrossMinor, order.GrossMinor);
    }

    [Fact]
    public async Task A_member_checks_out_with_and_without_a_business_block_and_reads_it_back_on_the_order()
    {
        const string currency = "QBB";
        var storefrontId = await SeedStorefrontAsync(currency);
        var productId = await SeedProductAsync(2_500, currency);
        var memberId = Guid.CreateVersion7();
        const string email = "member-buyer@example.com";

        using var member = Shopper(storefrontId);
        member.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(memberId, "customer", email));

        (await member.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var plain = await CheckoutAsync(member, new { email, shippingAddress = ShipTo });

        (await member.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var withBlock = await CheckoutAsync(member, new
        {
            email,
            shippingAddress = ShipTo,
            buyer = new { companyName = "Acme Pty Ltd", taxId = "51 824 753 556", taxIdScheme = (int)TaxIdScheme.Abn },
        });

        Assert.Equal((plain.TaxMinor, plain.GrossMinor), (withBlock.TaxMinor, withBlock.GrossMinor));
        var attempt = await AttemptAsync(withBlock.OrderId);
        Assert.Equal(memberId, attempt.UserId);
        Assert.Equal(("Acme Pty Ltd", "51824753556", (TaxIdScheme?)TaxIdScheme.Abn, "AU"),
            (attempt.BuyerCompanyName, attempt.BuyerTaxId, attempt.BuyerTaxIdScheme, attempt.BuyerTaxIdJurisdiction));
        // No billing address given → billed to the ship-to address.
        Assert.Equal(("Ship Name", "DE"), (attempt.BillName, attempt.BillCountry));

        await SettleAsync(member, plain);
        await SettleAsync(member, withBlock);

        // The member's own order detail returns both: the business block (scheme as a NUMBER) and the bill-to.
        using var detail = JsonDocument.Parse(await member.GetStringAsync($"/orders/{withBlock.OrderId}"));
        var buyer = detail.RootElement.GetProperty("buyer");
        Assert.Equal("Acme Pty Ltd", buyer.GetProperty("companyName").GetString());
        Assert.Equal("51824753556", buyer.GetProperty("taxId").GetString());
        Assert.Equal(JsonValueKind.Number, buyer.GetProperty("taxIdScheme").ValueKind);
        Assert.Equal((int)TaxIdScheme.Abn, buyer.GetProperty("taxIdScheme").GetInt32());
        Assert.Equal("Ship Name", detail.RootElement.GetProperty("billingAddress").GetProperty("name").GetString());

        // Negative control: the consumer order of the same member has a bill-to but no business block.
        using var plainDetail = JsonDocument.Parse(await member.GetStringAsync($"/orders/{plain.OrderId}"));
        Assert.Equal(JsonValueKind.Null, plainDetail.RootElement.GetProperty("buyer").ValueKind);
        Assert.Equal("Ship Name", plainDetail.RootElement.GetProperty("billingAddress").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("Acme GmbH", "DE12345678", (int)TaxIdScheme.EuVat, "buyer.taxId")] // DE needs 9 digits
    [InlineData("Acme GmbH", "DE123456789", null, "buyer.taxIdScheme")] // a tax ID needs its scheme
    [InlineData("", "DE123456789", (int)TaxIdScheme.EuVat, "buyer.companyName")] // a business block needs its company
    [InlineData("Acme Inc", "12-3456789", (int)TaxIdScheme.UsStateSalesTaxId, "buyer.taxIdJurisdiction")] // DE bill-to: no state
    [InlineData("Acme GmbH", "DE123456789", 42, "buyer.taxIdScheme")] // not a scheme
    public async Task An_invalid_business_block_is_a_400_naming_the_field_and_books_nothing(
        string companyName, string taxId, int? scheme, string expectedField)
    {
        const string currency = "QBC";
        var storefrontId = await SeedStorefrontAsync(currency);
        var productId = await SeedProductAsync(1_000, currency);
        var email = $"refused-{Guid.NewGuid():N}@example.com";

        using var shopper = Shopper(storefrontId);
        (await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 })).EnsureSuccessStatusCode();
        var response = await shopper.PostAsJsonAsync("/checkout", new
        {
            email,
            shippingAddress = ShipTo,
            buyer = new { companyName, taxId, taxIdScheme = scheme },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)CheckoutBlock.InvalidBuyerIdentity, problem.RootElement.GetProperty("checkoutBlock").GetInt32());
        Assert.Equal(expectedField, problem.RootElement.GetProperty("field").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(expectedField, out _));
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("detail").GetString()));

        // Nothing booked, and the cart is still there to fix and retry.
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        Assert.False(await db.CheckoutAttempts.AsNoTracking().AnyAsync(a => a.Email == email));
        using var cart = JsonDocument.Parse(await shopper.GetStringAsync("/cart/"));
        Assert.Equal(1, cart.RootElement.GetProperty("items").GetArrayLength());
    }

    private HttpClient Shopper(Guid storefrontId)
    {
        var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
        return client;
    }

    private static async Task<CheckoutResponseDto> CheckoutAsync(HttpClient shopper, object body)
    {
        var response = await shopper.PostAsJsonAsync("/checkout", body);
        Assert.True(response.IsSuccessStatusCode, $"checkout: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
    }

    private async Task<CheckoutAttempt> AttemptAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return await db.CheckoutAttempts.AsNoTracking().SingleAsync(a => a.Id == orderId);
    }

    private async Task<Order> OrderAsync(Guid orderId)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
    }

    private async Task<Guid> SeedProductAsync(long priceMinor, string currency)
    {
        var id = Guid.CreateVersion7();
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        db.ProductCopies.Add(new ProductCopy
        {
            ProductId = id,
            Slug = $"p-{id:N}",
            Title = "Buyer Identity Product",
            MinPriceMinor = priceMinor,
            Currency = currency,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedStorefrontAsync(string currency)
    {
        var storefrontId = Guid.CreateVersion7();
        await fixture.PublishAsync(new StorefrontConfigChanged(
            storefrontId, TenantId, $"Buyer Identity {currency}", currency, 1_000, IsLive: true, TaxInclusive: false, DiscountBps: 0));
        await Eventually.UntilAsync(
            async () =>
            {
                using var scope = fixture.Ordering.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
                var copy = await db.StorefrontTaxCopies.AsNoTracking().FirstOrDefaultAsync(c => c.StorefrontId == storefrontId);
                return copy is { IsLive: true, TaxRateBasisPoints: 1_000 };
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
