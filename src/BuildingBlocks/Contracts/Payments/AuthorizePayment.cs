namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// Request/response (not fire-and-forget): the checkout endpoint asks Payments to
/// create a payment intent, and blocks only until the intent exists (api.md §3).
/// NetMinor is the total to charge (items + shipping + tax); TaxMinor is the tax portion of it,
/// carried through so the sale can be booked as net revenue + a tax liability (per-storefront books).
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
    string? PaymentOption = null,
    // The storefront this order belongs to (phase 2). Persisted on the Payment so the sale posts to the
    // storefront's own revenue/tax ledger accounts (per-storefront books). Optional → back-compatible.
    Guid? StorefrontId = null,
    // Tax portion of NetMinor (computed by Ordering at checkout). Persisted on the Payment so the ledger
    // splits revenue (net) from tax collected (liability) instead of booking the whole gross as revenue.
    long TaxMinor = 0);

public record AuthorizePaymentResult(string PaymentIntentId, string ClientSecret, long GrossMinor, long TaxMinor);
