namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Why a cart cannot be checked out (ADR-0059). Members 1–2 are the storefront-level gates: reported by
/// <c>GET /cart/summary</c> so the storefront can say so BEFORE the shopper pays, and enforced by checkout
/// with <see cref="CheckoutGate"/>'s message. Members 3+ are the remaining checkout refusals: every
/// <c>POST /checkout</c> refusal is RFC 9457 problem+json carrying its member as the numeric
/// <c>checkoutBlock</c> extension, so a client branches on the code, never on the text. The preview only
/// ever reports 0–2. Crosses HTTP as a NUMBER (platform invariant) — append new members, never renumber.
/// </summary>
public enum CheckoutBlock
{
    None = 0,

    /// <summary>The cart's currency is not the storefront's (ADR-0038: storefront ↔ currency is 1:1).</summary>
    CurrencyMismatch = 1,

    /// <summary>A line's only covering offers on this storefront + currency are from unapproved suppliers.</summary>
    SupplyUnavailable = 2,

    /// <summary>The cart has no lines.</summary>
    CartEmpty = 3,

    /// <summary>No real storefront was resolved (the gateway's synthetic default store) — every order needs one.</summary>
    StorefrontRequired = 4,

    /// <summary>The cart holds lines in more than one currency (legacy data; adds are single-currency).</summary>
    MixedCurrencyCart = 5,

    /// <summary>The ship-to country is outside the storefront's ship-to allowlist.</summary>
    ShipToNotServed = 6,

    /// <summary>Collect at warehouse was requested for a cart with no warehouse-fulfilled line.</summary>
    CollectNotEligible = 7,

    /// <summary>The selected shipping rate is malformed (negative amount, or no service/expiry).</summary>
    InvalidShippingSelection = 8,

    /// <summary>The selected shipping quote has expired — the client should re-quote.</summary>
    ShippingQuoteExpired = 9,

    /// <summary>The entered coupon does not apply (or lost the last redemption); see <c>couponStatus</c>.</summary>
    CouponRefused = 10,

    /// <summary>The storefront is Draft/Paused/Archived — not open for orders.</summary>
    StorefrontNotOpen = 11,

    /// <summary>A subscription line needs a signed-in member (never a guest).</summary>
    SubscriptionRequiresMember = 12,

    /// <summary>A subscription line needs a verified email address.</summary>
    SubscriptionRequiresVerifiedEmail = 13,

    /// <summary>A subscription line needs a saved payment method or direct-debit mandate.</summary>
    SubscriptionRequiresSavedPaymentMethod = 14,

    /// <summary>A catalog price drifted since the cart was priced (409) — the cart was re-priced; review it.</summary>
    PricesChanged = 15,

    /// <summary>The payment service did not answer in time (503) — nothing was booked; retry.</summary>
    PaymentUnavailable = 16,
}

/// <summary>
/// Why a cart mutation (<c>POST /cart/items</c>, <c>PUT /cart/items/…</c>) was refused. Every cart refusal
/// is RFC 9457 problem+json carrying its member as the numeric <c>cartRefusal</c> extension — the same
/// shape as checkout's <see cref="CheckoutBlock"/>. Crosses HTTP as a NUMBER — append, never renumber.
/// </summary>
public enum CartRefusal
{
    None = 0,

    /// <summary>No product with that id is in the catalog copy (404).</summary>
    UnknownProduct = 1,

    /// <summary>The product has no variant with that id (404).</summary>
    UnknownVariant = 2,

    /// <summary>The product has no price in the requested currency, so it is not sold there (404).</summary>
    NotPricedInCurrency = 3,

    /// <summary>The cart already holds another currency (409) — carts are single-currency.</summary>
    CurrencyConflict = 4,

    /// <summary>The line being updated is not in the cart (404).</summary>
    LineNotFound = 5,
}

/// <summary>
/// The storefront-level checkout gates, kept pure so checkout and the cart preview share one wording and
/// one rule (ADR-0059).
/// </summary>
public static class CheckoutGate
{
    /// <summary>
    /// The currency of the first cart line that the storefront does not sell in, or null when every line is
    /// in the storefront's currency. A storefront with no projected copy (the tenant-default context, see
    /// ADR-0055) has no known currency and is not gated — the same fallback the tax resolution keeps.
    /// </summary>
    public static string? MismatchedCurrency(IEnumerable<string> cartCurrencies, StorefrontTaxCopy? storefront)
    {
        if (storefront is null || string.IsNullOrWhiteSpace(storefront.Currency))
        {
            return null;
        }

        return cartCurrencies.FirstOrDefault(c => !string.Equals(c, storefront.Currency, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The 400 checkout returns (and the preview reports) for a cart in another currency.</summary>
    public static string CurrencyMismatchMessage(string cartCurrency, string storefrontCurrency) =>
        $"Cart is in {cartCurrency.ToUpperInvariant()}; this store sells in {storefrontCurrency.ToUpperInvariant()} — empty the cart to shop here.";

    /// <summary>The 400 checkout returns (and the preview reports) for a line with no approved supply here.</summary>
    public static string SupplyUnavailableMessage(string title) =>
        $"{title} is currently unavailable on this store; its supplier is not approved. Remove it to check out.";
}
