using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Api.Endpoints;

/// <summary>
/// The ONE place cart and checkout refusals are shaped (AGENTS.md "Error Handling", api.md §4): every
/// refusal is RFC 9457 <c>application/problem+json</c> with a stable <c>title</c> per code, the shopper-facing
/// sentence as <c>detail</c>, and a numeric machine-readable extension — <c>checkoutBlock</c>
/// (<see cref="CheckoutBlock"/>, ADR-0059's pattern) for checkout, <c>cartRefusal</c> (<see cref="CartRefusal"/>)
/// for cart mutations — so a client branches on the code and never parses text. The shared
/// <c>AddApiProblemDetails</c> customizer adds the <c>traceId</c>.
/// </summary>
public static class OrderingProblems
{
    /// <summary>A refused checkout. Status comes from the code (400, except 409 price drift and 503 payment).</summary>
    public static ProblemHttpResult CheckoutRefused(
        CheckoutBlock block, string detail, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var all = new Dictionary<string, object?> { ["checkoutBlock"] = (int)block };
        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                all[key] = value;
            }
        }

        return TypedResults.Problem(detail: detail, statusCode: StatusFor(block), title: TitleFor(block), extensions: all);
    }

    /// <summary>A refused cart mutation (404 for an unknown product/variant/line or an unpriced currency, 409 for a currency conflict).</summary>
    public static ProblemHttpResult CartRefused(CartRefusal refusal, string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusFor(refusal),
            title: TitleFor(refusal),
            extensions: new Dictionary<string, object?> { ["cartRefusal"] = (int)refusal });

    public static int StatusFor(CheckoutBlock block) => block switch
    {
        CheckoutBlock.PricesChanged => StatusCodes.Status409Conflict,
        CheckoutBlock.PaymentUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>The stable title per code — RFC 9457: the same for every occurrence of a problem type.</summary>
    public static string TitleFor(CheckoutBlock block) => block switch
    {
        // Unchanged since #282 (ADR-0059): both storefront gates share it.
        CheckoutBlock.CurrencyMismatch or CheckoutBlock.SupplyUnavailable => "This cart cannot be checked out on this storefront.",
        CheckoutBlock.CartEmpty => "The cart is empty.",
        CheckoutBlock.StorefrontRequired => "A storefront is required to check out.",
        CheckoutBlock.MixedCurrencyCart => "The cart contains more than one currency.",
        CheckoutBlock.ShipToNotServed => "This storefront does not ship to the destination.",
        CheckoutBlock.CollectNotEligible => "Collect at warehouse is not available for this cart.",
        CheckoutBlock.InvalidShippingSelection => "The selected shipping option is invalid.",
        CheckoutBlock.ShippingQuoteExpired => "The selected shipping quote has expired.",
        CheckoutBlock.CouponRefused => "The coupon code cannot be applied.",
        CheckoutBlock.StorefrontNotOpen => "This storefront is not open for orders.",
        CheckoutBlock.SubscriptionRequiresMember => "A subscription requires a signed-in member.",
        CheckoutBlock.SubscriptionRequiresVerifiedEmail => "A subscription requires a verified email address.",
        CheckoutBlock.SubscriptionRequiresSavedPaymentMethod => "A subscription requires a saved payment method.",
        CheckoutBlock.PricesChanged => "Prices changed since the cart was priced.",
        CheckoutBlock.PaymentUnavailable => "The payment service is unavailable.",
        _ => "The checkout was refused.",
    };

    public static int StatusFor(CartRefusal refusal) => refusal switch
    {
        CartRefusal.CurrencyConflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status404NotFound,
    };

    public static string TitleFor(CartRefusal refusal) => refusal switch
    {
        CartRefusal.UnknownProduct => "Product not found.",
        CartRefusal.UnknownVariant => "Variant not found.",
        CartRefusal.NotPricedInCurrency => "The product is not sold in this currency.",
        CartRefusal.CurrencyConflict => "The cart is in another currency.",
        CartRefusal.LineNotFound => "The item is not in the cart.",
        _ => "The cart change was refused.",
    };
}
