using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Providers;

/// <summary>
/// Keyed provider resolver + mode gate (ADR-0039), replacing the startup singleton selection.
/// Adapters self-register in DI as <see cref="IPaymentProvider"/>; this resolves them by lowercase
/// <see cref="IPaymentProvider.ProviderKey"/>. In LocalMock the mock adapter is selected regardless
/// of the account's declared provider; the mode resolver fails closed on unsafe combinations.
/// </summary>
public sealed class PaymentProviderRegistry(
    IEnumerable<IPaymentProvider> adapters,
    PaymentModeResolver modeResolver) : IPaymentProviderRegistry
{
    private const string MockKey = "mock";

    public IPaymentProvider Resolve(PaymentAccountSnapshot account)
    {
        var mode = modeResolver.Resolve(account); // throws on unsafe host×account combinations
        return mode == PaymentMode.LocalMock
            ? Adapter(MockKey)
            : Adapter(account.Provider.ToLowerInvariant());
    }

    public IPaymentProvider ResolveDefault() => Resolve(modeResolver.DefaultAccountForHost());

    public IPaymentProvider ResolveByKey(string providerKey) => Adapter(providerKey.ToLowerInvariant());

    private IPaymentProvider Adapter(string key) =>
        adapters.SingleOrDefault(a => a.ProviderKey == key)
        ?? throw new PaymentConfigurationException($"No payment provider is registered for key '{key}'.");
}
