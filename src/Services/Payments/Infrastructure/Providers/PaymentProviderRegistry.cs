using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers.Mock;

namespace ThreeCommerce.Payments.Infrastructure.Providers;

/// <summary>
/// Keyed provider resolver + mode gate (ADR-0039), replacing the startup singleton selection.
/// Adapters self-register in DI as <see cref="IPaymentProvider"/>; this resolves them by lowercase
/// <see cref="IPaymentProvider.ProviderKey"/>. In LocalMock the mock adapter is selected regardless
/// of the account's declared provider; in Sandbox the real adapter is wrapped so it ALSO emits the
/// TEST-ONLY payload capture (pay_3); the mode resolver fails closed on unsafe combinations.
/// </summary>
public sealed class PaymentProviderRegistry(
    IEnumerable<IPaymentProvider> adapters,
    PaymentModeResolver modeResolver,
    IMockPaymentCapture? capture = null) : IPaymentProviderRegistry
{
    private const string MockKey = "mock";

    public IPaymentProvider Resolve(PaymentAccountSnapshot account)
    {
        var mode = modeResolver.Resolve(account); // throws on unsafe host×account combinations
        if (mode == PaymentMode.LocalMock)
        {
            return Adapter(MockKey);
        }

        var provider = Adapter(account.Provider.ToLowerInvariant());

        // Sandbox ALSO sends the TEST-ONLY payload email (pay_3, ADR-0039): wrap the real adapter so
        // the capture is emitted alongside the real authorize/refund. Production is never wrapped.
        return mode == PaymentMode.Sandbox && capture is not null
            ? new SandboxCaptureProvider(provider, capture)
            : provider;
    }

    public IPaymentProvider ResolveDefault() => Resolve(modeResolver.DefaultAccountForHost());

    public IPaymentProvider ResolveByKey(string providerKey) => Adapter(providerKey.ToLowerInvariant());

    private IPaymentProvider Adapter(string key) =>
        adapters.SingleOrDefault(a => a.ProviderKey == key)
        ?? throw new PaymentConfigurationException($"No payment provider is registered for key '{key}'.");
}
