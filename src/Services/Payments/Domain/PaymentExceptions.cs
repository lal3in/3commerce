using ThreeCommerce.BuildingBlocks.Contracts.Abstractions;

namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// Thrown when the resolved runtime mode is unsafe for the account (fail-closed, ADR-0039):
/// a Production host asked to run a Test account, or a Sandbox host asked to run a Live account.
/// Server-side misconfiguration → 500 with a machine-readable code.
/// </summary>
public sealed class PaymentModeException(string message) : Exception(message), IProblemException
{
    public int StatusCode => 500;
    public string ErrorCode => "payment_mode_unsafe";
}

/// <summary>
/// Thrown for a mis-configured credential (missing key, or a key whose prefix does not match the
/// resolved mode — e.g. an <c>sk_live_</c> key under a Sandbox host). Maps to
/// <see cref="PaymentErrorCode.ConfigurationError"/>. Mirrors DevSecretGuard's fail-closed refusal.
/// </summary>
public sealed class PaymentConfigurationException(string message) : Exception(message), IProblemException
{
    public int StatusCode => 500;
    public string ErrorCode => "payment_configuration";
}

/// <summary>
/// A provider authorization/refund failure surfaced at an HTTP boundary. Carries the typed
/// <see cref="PaymentError"/> taxonomy (plan item: error-handling strategy) and maps each code to
/// the right HTTP status — a declined card is the client's 402, a rate-limit is 429, an unavailable
/// provider is 502, a config error is 500. The message is provider-safe (no PAN/CVV/token).
/// </summary>
public sealed class PaymentAuthorizationException(PaymentError error)
    : Exception(error.Message), IProblemException
{
    public PaymentError Error { get; } = error;

    public bool Retryable => Error.Retryable;

    public int StatusCode => Error.Code switch
    {
        PaymentErrorCode.CardDeclined => 402,
        PaymentErrorCode.ExpiredCard => 402,
        PaymentErrorCode.InsufficientFunds => 402,
        PaymentErrorCode.AuthenticationRequired => 402,
        PaymentErrorCode.RateLimited => 429,
        PaymentErrorCode.ProviderUnavailable => 502,
        PaymentErrorCode.ProcessingError => 502,
        PaymentErrorCode.ConfigurationError => 500,
        _ => 500,
    };

    public string ErrorCode => Error.Code switch
    {
        PaymentErrorCode.CardDeclined => "card_declined",
        PaymentErrorCode.ExpiredCard => "expired_card",
        PaymentErrorCode.InsufficientFunds => "insufficient_funds",
        PaymentErrorCode.AuthenticationRequired => "authentication_required",
        PaymentErrorCode.RateLimited => "rate_limited",
        PaymentErrorCode.ProviderUnavailable => "provider_unavailable",
        PaymentErrorCode.ProcessingError => "processing_error",
        PaymentErrorCode.ConfigurationError => "configuration_error",
        _ => "payment_error",
    };
}
