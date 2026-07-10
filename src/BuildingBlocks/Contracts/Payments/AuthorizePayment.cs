namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// Request/response (not fire-and-forget): the checkout endpoint asks Payments to
/// price tax + create a payment intent, and blocks only until the intent exists (api.md §3).
/// NetMinor is the pre-tax total (items + shipping); Payments computes tax and the gross.
/// </summary>
public record AuthorizePayment(
    Guid OrderId,
    long NetMinor,
    string Currency,
    string IdempotencyKey,
    Guid? UserId = null,
    Guid? SavedPaymentMethodId = null,
    bool SavePaymentMethod = false,
    string? ShipCountry = null,
    // Checkout's normalized paymentOption (Stripe|CreditCard|ApplePay|GooglePay|PayPal). Payments maps
    // it to the numeric PaymentMethodKind server-side (pay_4/ADR-0039); wallets settle through the PSP.
    string? PaymentOption = null);

public record AuthorizePaymentResult(string PaymentIntentId, string ClientSecret, long GrossMinor, long TaxMinor);
