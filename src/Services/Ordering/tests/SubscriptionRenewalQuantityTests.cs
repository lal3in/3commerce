using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure.Consumers;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// subscription_renewal_price_quantity (owner decision 2026-10-27, ADR-0057 amendment): a subscription line renews at
/// unit × quantity − the line's renewal discount — the figure the cart's "renews at" preview shows
/// (<c>line.TotalMinor − RenewalLineDiscountsMinor[i]</c>) and the basis the first period was charged on. The renewal
/// discount is allocated per LINE, so it is subtracted once from the line total, never per unit. Before the decision a
/// quantity-2 line previewed 2× but renewed at 1×.
/// </summary>
public class SubscriptionRenewalQuantityTests
{
    [Theory]
    // unit, qty, line renewal discount, expected renewal price
    [InlineData(2000, 1, 0, 2000)]
    [InlineData(2000, 2, 0, 4000)]     // the reported bug: was 2000
    [InlineData(2000, 3, 1500, 4500)]  // a 25% permanent promotion allocated to the line (6000 × 25%)
    [InlineData(999, 3, 1, 2996)]      // a line discount that does not divide by the quantity is still exact
    [InlineData(1000, 2, 2500, 0)]     // never negative
    public void A_subscription_line_renews_at_its_line_total_less_its_renewal_discount(long unit, int quantity, long renewalDiscount, long expected)
    {
        var line = Line(unit, quantity, renewalDiscount);

        Assert.Equal(expected, line.RenewalPriceMinor());
    }

    [Fact]
    public void The_subscription_request_carries_the_line_total_and_the_quantity()
    {
        var order = Order();
        var line = Line(2000, 2, 500);

        var request = OrderStatusConsumer.SubscriptionRequestFor(order, line, taxInclusive: false, taxExempt: true);

        Assert.Equal(3500, request.PriceMinor);  // 2 × 2000 − 500, what the preview showed
        Assert.Equal(2, request.Quantity);
        Assert.Equal(order.TenantId, request.TenantId);
        Assert.Equal(order.Id, request.OrderId);
        Assert.Equal(order.Email, request.CustomerEmail);
        Assert.Equal(order.StorefrontId, request.StorefrontId);
        Assert.Equal("USD", request.Currency);
        Assert.Equal(line.ProductId, request.ProductId);
        Assert.Equal(line.VariantId, request.VariantId);
        Assert.Equal(BillingPeriod.Monthly, request.BillingPeriod);
        Assert.False(request.TaxInclusive);
        Assert.True(request.TaxExempt);
    }

    private static OrderLine Line(long unit, int quantity, long renewalDiscount) => new()
    {
        Id = Guid.CreateVersion7(),
        ProductId = Guid.CreateVersion7(),
        VariantId = Guid.CreateVersion7(),
        Title = "Plan",
        UnitPriceMinor = unit,
        Quantity = quantity,
        DiscountMinor = renewalDiscount,
        RenewalDiscountMinor = renewalDiscount,
        BillingMode = BillingMode.Recurring,
        BillingPeriod = BillingPeriod.Monthly,
    };

    private static Order Order() => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        StorefrontId = Guid.CreateVersion7(),
        Email = "subscriber@example.test",
        Status = OrderStatus.Confirmed,
        Currency = "USD",
        ShipName = "S",
        ShipLine1 = "1 St",
        ShipCity = "C",
        ShipPostcode = "0000",
        ShipCountry = "US",
    };
}
