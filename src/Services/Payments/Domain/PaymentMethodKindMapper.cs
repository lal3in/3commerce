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
}
