namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// Maps Ordering's checkout <c>paymentOption</c> string (Stripe|CreditCard|ApplePay|GooglePay|PayPal —
/// CheckoutEndpoints.NormalizePaymentOption) to the numeric <see cref="PaymentMethodKind"/> carried on
/// <see cref="PaymentRequest"/> (ADR-0039). Apple Pay / Google Pay are wallet UIs tokenized THROUGH the
/// account's PSP, so the method kind is recorded for analytics/receipts and passed to the adapter, but
/// it does not change which provider settles — that is still the account's PSP. Unknown/absent values
/// fall back to <see cref="PaymentMethodKind.Card"/>.
/// </summary>
public static class PaymentMethodKindMapper
{
    public static PaymentMethodKind From(string? paymentOption) => (paymentOption ?? string.Empty).Trim() switch
    {
        "ApplePay" => PaymentMethodKind.ApplePay,
        "GooglePay" => PaymentMethodKind.GooglePay,
        "PayPal" => PaymentMethodKind.PayPal,
        "Afterpay" => PaymentMethodKind.Afterpay,
        "Polar" => PaymentMethodKind.Polar,
        _ => PaymentMethodKind.Card, // "Stripe" / "CreditCard" / unknown → card
    };

    /// <summary>
    /// Routes a <see cref="PaymentMethodKind"/> to the provider key that should SETTLE it (ADR-0039).
    /// Card / Apple Pay / Google Pay are card-PSP methods — Apple/Google Pay are wallet UIs tokenized
    /// THROUGH the card PSP — so they return <c>null</c>, meaning "settle on the card PSP / the tenant
    /// default account's provider" (today stripe). PayPal, Afterpay and Polar are standalone PSPs, so
    /// they settle on their own provider and post to <c>cash.{provider}</c>. Keys are lowercase and
    /// consistent with <c>LedgerProviders.Known</c> and each adapter's <c>ProviderKey</c>.
    /// </summary>
    public static string? SettlingProviderFor(PaymentMethodKind methodKind) => methodKind switch
    {
        PaymentMethodKind.PayPal => "paypal",
        PaymentMethodKind.Afterpay => "afterpay",
        PaymentMethodKind.Polar => "polar",
        _ => null, // Card / ApplePay / GooglePay → the card PSP (default account provider)
    };
}
