namespace ThreeCommerce.Ordering.Domain;

/// <summary>
/// Why a cart cannot be checked out on the storefront it is being viewed on (ADR-0059). Reported by
/// <c>GET /cart/summary</c> so the storefront can say so BEFORE the shopper pays, and enforced by checkout
/// as a 400 with <see cref="CheckoutGate"/>'s message. Crosses HTTP as a NUMBER (platform invariant) —
/// append new members, never renumber.
/// </summary>
public enum CheckoutBlock
{
    None = 0,

    /// <summary>The cart's currency is not the storefront's (ADR-0038: storefront ↔ currency is 1:1).</summary>
    CurrencyMismatch = 1,

    /// <summary>A line's only covering offers on this storefront + currency are from unapproved suppliers.</summary>
    SupplyUnavailable = 2,
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
