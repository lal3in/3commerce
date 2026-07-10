namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// TEST-ONLY event (pay_3, ADR-0039): the payment request that WOULD have been sent to the provider,
/// captured in LocalMock and Sandbox so the Notifications worker can render a clearly-labelled
/// "TEST ONLY / MOCK PAYMENT" email to <c>Payments:MockEmailTo</c>. It is <b>never</b> published in
/// Production — the boot-time <c>PaymentModeGuard</c> refuses the mock/email config there, so there is
/// no path to this event with real money in play.
/// <para>
/// The payload is already redacted before publish (<c>PayloadRedactor</c>): never PAN, CVV, wallet
/// tokens or raw secrets; provider payment-method refs are reduced to a last-4 marker. Display fields
/// are carried as plain strings/numbers (no enums on the wire); the numeric method-kind lives inside
/// <see cref="RedactedPayloadJson"/>.
/// </para>
/// </summary>
public record MockPaymentCaptured(
    string Operation,           // "Authorize" | "Refund"
    Guid OrderId,               // Guid.Empty for a refund (the refund path carries no order context)
    Guid TenantId,              // Guid.Empty for a refund
    string Provider,            // resolved provider key, e.g. "mock" | "stripe"
    string MethodKind,          // display label, e.g. "Card" | "ApplePay" ("n/a" for a refund)
    long AmountMinor,
    string Currency,            // empty for a refund (the RefundAsync seam carries no currency)
    string PaymentIntentId,
    string Mode,                // "LocalMock" | "Sandbox"
    string Scenario,            // simulated scenario name, e.g. "Success" ("n/a" for a refund)
    string RedactedPayloadJson, // the would-be provider payload, redacted, embedded verbatim in the email
    DateTimeOffset CapturedAt);
