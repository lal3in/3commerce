namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// Thrown when the resolved runtime mode is unsafe for the account (fail-closed, ADR-0039):
/// a Production host asked to run a Test account, or a Sandbox host asked to run a Live account.
/// </summary>
public sealed class PaymentModeException(string message) : Exception(message);

/// <summary>
/// Thrown for a mis-configured credential (missing key, or a key whose prefix does not match the
/// resolved mode — e.g. an <c>sk_live_</c> key under a Sandbox host). Maps to
/// <see cref="PaymentErrorCode.ConfigurationError"/>. Mirrors DevSecretGuard's fail-closed refusal.
/// </summary>
public sealed class PaymentConfigurationException(string message) : Exception(message);
