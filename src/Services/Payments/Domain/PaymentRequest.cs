namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// The payment method the shopper chose. Wire values are stable and numeric (platform invariant,
/// matches <see cref="PaymentWebhookKind"/> / <see cref="PaymentProviderMode"/>). Maps from
/// Ordering's checkout paymentOption (Stripe|CreditCard|ApplePay|GooglePay|PayPal —
/// CheckoutEndpoints.NormalizePaymentOption). Apple/Google Pay are wallet UIs tokenized THROUGH a
/// PSP (ADR-0039), not standalone providers.
/// </summary>
public enum PaymentMethodKind
{
    Card = 1,       // credit/debit, incl. "Stripe"/"CreditCard" default
    ApplePay = 2,   // wallet — tokenized through a PSP
    GooglePay = 3,  // wallet — tokenized through a PSP
    PayPal = 4,     // PSP-native
    Afterpay = 5,   // BNPL PSP
    Polar = 6,      // merchant-of-record PSP
}

/// <summary>
/// The RESOLVED runtime operating mode (see <c>PaymentModeResolver</c>). Distinct from
/// <see cref="PaymentProviderMode"/> (Test|Live), which is the per-tenant "which credential set"
/// fact on <see cref="PaymentAccount"/>. ADR-0039.
/// </summary>
public enum PaymentMode
{
    LocalMock = 1,  // no external calls; mock adapter; TEST-only capture
    Sandbox = 2,    // provider test credentials; TEST-only capture
    Production = 3, // live credentials; no capture email; redacted logs
}

/// <summary>The outcome of an authorize attempt, provider-agnostic.</summary>
public enum PaymentOutcome
{
    Succeeded = 1,
    RequiresAction = 2, // client must confirm (e.g. 3DS) using the client secret
    Failed = 3,
    Cancelled = 4,
}

/// <summary>
/// Typed provider error taxonomy → RFC-7807 problem-details. Adapters translate provider SDK/HTTP
/// exceptions into this so raw provider exception text never leaks to clients. See the error table
/// in the plan for the HTTP mapping.
/// </summary>
public enum PaymentErrorCode
{
    CardDeclined = 1,
    ExpiredCard = 2,
    AuthenticationRequired = 3,
    InsufficientFunds = 4,
    ProcessingError = 5,
    RateLimited = 6,
    ProviderUnavailable = 7,
    ConfigurationError = 8,
}

/// <summary>LocalMock simulation selector; honored ONLY in LocalMock (ignored otherwise).</summary>
public enum MockScenario
{
    Success = 1,
    Failure = 2,
    DeclinedCard = 3,
    ExpiredCard = 4,
    Requires3ds = 5,
    Cancelled = 6,
}

/// <summary>
/// Everything an adapter needs to authorize a payment, provider-agnostic. Carries the resolved
/// <see cref="PaymentAccountSnapshot"/> so the registry can key on (Provider, PaymentAccount) and a
/// pay_4 adapter can pick the right credentials. <see cref="WalletToken"/> and any provider
/// method/customer refs are redacted from logs and the TEST-only email (never PAN/CVV/token).
/// </summary>
public sealed record PaymentRequest(
    Guid OrderId,
    long AmountMinor,               // gross the shopper pays (Ordering-owned tax already inside; never re-taxed)
    string Currency,
    string IdempotencyKey,
    PaymentMethodKind MethodKind,
    PaymentAccountSnapshot Account,
    string? ProviderCustomerId = null,
    string? ProviderPaymentMethodId = null,
    string? WalletToken = null,     // Apple/Google Pay tokenization blob — never logged/emailed
    bool SetupFutureUsage = false,
    MockScenario? Scenario = null); // honored ONLY in LocalMock

/// <summary>The provider-agnostic authorize result. Mirrors the old intent id + client secret plus a typed outcome/error.</summary>
public sealed record PaymentResponse(
    string PaymentIntentId,
    string? ClientSecret,
    PaymentOutcome Outcome,
    PaymentError? Error = null);

/// <summary>Typed provider error → problem-details (never leaks provider exception text raw).</summary>
public sealed record PaymentError(PaymentErrorCode Code, string Message, bool Retryable);
