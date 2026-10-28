using Microsoft.AspNetCore.Http;
using ThreeCommerce.Ordering.Api.Endpoints;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// Every cart/checkout/order refusal is RFC 9457 problem+json (AGENTS.md "Error Handling"): these facts pin the
/// wire codes clients branch on (they cross HTTP as numbers and are never renumbered), the status per code,
/// and that each code has its own stable title.
/// </summary>
public class OrderingProblemsTests
{
    [Fact]
    public void CheckoutBlock_wire_values_are_stable()
    {
        Assert.Equal(
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17],
            Enum.GetValues<CheckoutBlock>().Select(b => (int)b));
        Assert.Equal(3, (int)CheckoutBlock.CartEmpty);
        Assert.Equal(10, (int)CheckoutBlock.CouponRefused);
        Assert.Equal(15, (int)CheckoutBlock.PricesChanged);
        Assert.Equal(16, (int)CheckoutBlock.PaymentUnavailable);
        Assert.Equal(17, (int)CheckoutBlock.InvalidBuyerIdentity);
    }

    [Fact]
    public void CartRefusal_wire_values_are_stable() =>
        Assert.Equal([0, 1, 2, 3, 4, 5], Enum.GetValues<CartRefusal>().Select(r => (int)r));

    [Theory]
    [InlineData(CheckoutBlock.PricesChanged, StatusCodes.Status409Conflict)]
    [InlineData(CheckoutBlock.PaymentUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(CheckoutBlock.CartEmpty, StatusCodes.Status400BadRequest)]
    [InlineData(CheckoutBlock.CurrencyMismatch, StatusCodes.Status400BadRequest)]
    [InlineData(CheckoutBlock.CouponRefused, StatusCodes.Status400BadRequest)]
    [InlineData(CheckoutBlock.InvalidBuyerIdentity, StatusCodes.Status400BadRequest)]
    public void Checkout_refusal_status_follows_the_code(CheckoutBlock block, int status) =>
        Assert.Equal(status, OrderingProblems.StatusFor(block));

    [Theory]
    [InlineData(CartRefusal.CurrencyConflict, StatusCodes.Status409Conflict)]
    [InlineData(CartRefusal.UnknownProduct, StatusCodes.Status404NotFound)]
    [InlineData(CartRefusal.LineNotFound, StatusCodes.Status404NotFound)]
    public void Cart_refusal_status_follows_the_code(CartRefusal refusal, int status) =>
        Assert.Equal(status, OrderingProblems.StatusFor(refusal));

    [Fact]
    public void Every_checkout_refusal_has_its_own_title_except_the_shared_storefront_gates()
    {
        var refusals = Enum.GetValues<CheckoutBlock>().Where(b => b != CheckoutBlock.None).ToList();
        var titles = refusals.Select(OrderingProblems.TitleFor).ToList();

        Assert.DoesNotContain("The checkout was refused.", titles); // every member is mapped
        // ADR-0059's two gates keep #282's shared title; everything else is distinct.
        Assert.Equal(OrderingProblems.TitleFor(CheckoutBlock.CurrencyMismatch), OrderingProblems.TitleFor(CheckoutBlock.SupplyUnavailable));
        Assert.Equal("This cart cannot be checked out on this storefront.", OrderingProblems.TitleFor(CheckoutBlock.CurrencyMismatch));
        Assert.Equal(refusals.Count - 1, titles.Distinct().Count());
    }

    [Fact]
    public void Every_cart_refusal_has_its_own_title()
    {
        var titles = Enum.GetValues<CartRefusal>().Where(r => r != CartRefusal.None).Select(OrderingProblems.TitleFor).ToList();
        Assert.DoesNotContain("The cart change was refused.", titles);
        Assert.Equal(titles.Count, titles.Distinct().Count());
    }

    [Fact]
    public void A_checkout_refusal_carries_its_code_detail_and_extra_extensions()
    {
        var result = OrderingProblems.CheckoutRefused(
            CheckoutBlock.CouponRefused, "Coupon code 'X' has expired.",
            new Dictionary<string, object?> { ["couponStatus"] = (int)CouponStatus.Expired });

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal("application/problem+json", result.ContentType);
        Assert.Equal("Coupon code 'X' has expired.", result.ProblemDetails.Detail);
        Assert.Equal("The coupon code cannot be applied.", result.ProblemDetails.Title);
        Assert.Equal(10, result.ProblemDetails.Extensions["checkoutBlock"]);
        Assert.Equal(5, result.ProblemDetails.Extensions["couponStatus"]);
    }

    [Fact]
    public void A_cart_refusal_carries_its_code() =>
        Assert.Equal(4, OrderingProblems.CartRefused(CartRefusal.CurrencyConflict, "x").ProblemDetails.Extensions["cartRefusal"]);

    [Fact]
    public void OrderRefusal_wire_values_are_stable()
    {
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<OrderRefusal>().Select(r => (int)r));
        // The supplier portal branches on this number to show "This order was just updated — refresh and try again."
        Assert.Equal(4, (int)OrderRefusal.ChangedConcurrently);
    }

    [Fact]
    public void Every_order_refusal_has_its_own_title()
    {
        var titles = Enum.GetValues<OrderRefusal>().Where(r => r != OrderRefusal.None).Select(OrderingProblems.TitleFor).ToList();
        Assert.DoesNotContain("The order change was refused.", titles);
        Assert.Equal(titles.Count, titles.Distinct().Count());
    }

    [Fact]
    public void A_concurrent_order_change_is_a_409_with_its_code_detail_and_current_version()
    {
        var result = OrderingProblems.OrderRefused(
            OrderRefusal.ChangedConcurrently, OrderingProblems.OrderChangedConcurrentlyDetail, currentVersion: 812u);

        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Equal("application/problem+json", result.ContentType);
        Assert.Equal("This order was just updated — refresh and try again.", result.ProblemDetails.Detail);
        Assert.Equal("The order was changed concurrently.", result.ProblemDetails.Title);
        Assert.Equal(4, result.ProblemDetails.Extensions["orderRefusal"]);
        Assert.Equal(812u, result.ProblemDetails.Extensions["currentVersion"]);
    }

    [Fact]
    public void An_order_refusal_without_a_known_version_omits_it()
    {
        var result = OrderingProblems.OrderRefused(OrderRefusal.NotConfirmed, "Only a confirmed order can be marked delivered.");

        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Equal(3, result.ProblemDetails.Extensions["orderRefusal"]);
        Assert.False(result.ProblemDetails.Extensions.ContainsKey("currentVersion"));
    }
}
