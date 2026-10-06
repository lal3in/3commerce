using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>ADR-0059: an order's currency is its storefront's currency (ADR-0038 storefront ↔ currency 1:1).</summary>
public class CheckoutGateTests
{
    private static StorefrontTaxCopy Store(string currency) =>
        new() { StorefrontId = Guid.NewGuid(), TenantId = Guid.NewGuid(), Currency = currency, IsLive = true };

    [Fact]
    public void A_cart_in_the_storefront_currency_passes() =>
        Assert.Null(CheckoutGate.MismatchedCurrency(["AUD", "aud"], Store("AUD")));

    [Fact]
    public void A_cart_in_another_currency_reports_that_currency() =>
        Assert.Equal("EUR", CheckoutGate.MismatchedCurrency(["EUR"], Store("AUD")));

    [Fact]
    public void A_storefront_without_a_projected_copy_is_not_gated() =>
        Assert.Null(CheckoutGate.MismatchedCurrency(["EUR"], null));

    [Fact]
    public void The_message_names_both_currencies_and_the_remedy() =>
        Assert.Equal(
            "Cart is in EUR; this store sells in AUD — empty the cart to shop here.",
            CheckoutGate.CurrencyMismatchMessage("eur", "AUD"));
}
