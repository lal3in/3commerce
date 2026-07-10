namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// Resolves the <see cref="IPaymentProvider"/> for a payment (ADR-0039), replacing the startup
/// singleton selection. Resolution applies the <c>PaymentMode</c> gate: in LocalMock the mock
/// adapter is selected regardless of the account's declared provider (offline dev needs no
/// credentials); in Sandbox/Production the account's provider adapter is used, and the mode
/// resolver fails closed on unsafe host×account combinations.
/// </summary>
public interface IPaymentProviderRegistry
{
    /// <summary>Resolves by <paramref name="account"/>.Provider, applying the mode gate. Throws on unsafe combinations.</summary>
    public IPaymentProvider Resolve(PaymentAccountSnapshot account);

    /// <summary>
    /// Resolves the host's default provider (the successor to the old startup singleton): the mock
    /// adapter in LocalMock, else the configured default provider. For call sites that do not yet
    /// carry per-order account context (authorize/refund/subscription/overage/saved-cards).
    /// </summary>
    public IPaymentProvider ResolveDefault();

    /// <summary>Resolves by provider key for inbound webhooks (no account context). Throws if the key is unknown.</summary>
    public IPaymentProvider ResolveByKey(string providerKey);
}
