using Microsoft.Extensions.Hosting;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// Registry resolution (ADR-0039): resolves by lowercase provider key; LocalMock overrides the
/// account's declared provider with the mock adapter; unknown keys throw; ResolveDefault reproduces
/// the old startup singleton (mock in dev, the configured provider otherwise).
/// </summary>
public class PaymentProviderRegistryTests
{
    private sealed class StubProvider(string key) : IPaymentProvider
    {
        public string ProviderKey => key;
        public Task<PaymentResponse> AuthorizeAsync(PaymentRequest r, CancellationToken ct) =>
            Task.FromResult(new PaymentResponse("pi", "sec", PaymentOutcome.Succeeded));
        public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) => Task.FromResult("cus");
        public Task<SetupIntentResult> CreateSetupIntentAsync(string c, CancellationToken ct) => Task.FromResult(new SetupIntentResult("seti", "sec"));
        public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string id, CancellationToken ct) => Task.FromResult(new SavedPaymentMethodDetails(id, "visa", "4242", 12, 2030));
        public Task<ProviderRefundResult> RefundAsync(string i, long a, string k, CancellationToken ct) => Task.FromResult(new ProviderRefundResult("re", true));
        public PaymentWebhookEvent? ParseWebhook(string p, string s, IReadOnlyList<string> secrets) => null;
    }

    private static PaymentProviderRegistry Registry(string hostMode, string env)
    {
        var resolver = new PaymentModeResolver(PaymentTestSupport.Config(("Payments:Mode", hostMode)), PaymentTestSupport.Env(env));
        return new PaymentProviderRegistry([new StubProvider("mock"), new StubProvider("stripe")], resolver);
    }

    [Fact]
    public void Resolves_the_account_provider_in_production()
    {
        var provider = Registry("Production", Environments.Production).Resolve(PaymentTestSupport.Account(PaymentProviderMode.Live));
        Assert.Equal("stripe", provider.ProviderKey);
    }

    [Fact]
    public void LocalMock_overrides_the_declared_provider_with_the_mock_adapter()
    {
        var provider = Registry("LocalMock", Environments.Development).Resolve(PaymentTestSupport.Account(PaymentProviderMode.Live, provider: "stripe"));
        Assert.Equal("mock", provider.ProviderKey);
    }

    [Fact]
    public void ResolveByKey_is_case_insensitive_and_throws_on_unknown()
    {
        var registry = Registry("Production", Environments.Production);
        Assert.Equal("stripe", registry.ResolveByKey("STRIPE").ProviderKey);
        Assert.Throws<PaymentConfigurationException>(() => registry.ResolveByKey("paypal"));
    }

    [Fact]
    public void ResolveDefault_is_mock_in_dev_and_the_configured_provider_in_production()
    {
        Assert.Equal("mock", Registry("LocalMock", Environments.Development).ResolveDefault().ProviderKey);
        Assert.Equal("stripe", Registry("Production", Environments.Production).ResolveDefault().ProviderKey);
    }

    [Fact]
    public void Resolve_propagates_the_fail_closed_refusal()
    {
        var registry = Registry("Production", Environments.Production);
        Assert.Throws<PaymentModeException>(() => registry.Resolve(PaymentTestSupport.Account(PaymentProviderMode.Test)));
    }
}
