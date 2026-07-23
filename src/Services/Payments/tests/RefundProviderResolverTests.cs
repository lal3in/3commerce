using Microsoft.Extensions.Hosting;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// A refund must execute through the PSP that SETTLED the sale (the payment's stored provider), so a
/// PayPal/Polar/Afterpay sale refunds down its own rail instead of the host default (ADR-0039) — while
/// still failing safe: the SAME mode gate as authorize applies (mock in LocalMock), and an
/// unregistered/refused provider degrades to the host default rather than crashing the refund.
/// </summary>
public class RefundProviderResolverTests
{
    private static (IPaymentProviderRegistry Registry, PaymentModeResolver Resolver) Setup(
        string hostMode, string env, params string[] providerKeys)
    {
        var resolver = new PaymentModeResolver(PaymentTestSupport.Config(("Payments:Mode", hostMode)), PaymentTestSupport.Env(env));
        var adapters = providerKeys.Select(k => (IPaymentProvider)new StubProvider(k)).ToList();
        return (new PaymentProviderRegistry(adapters, resolver), resolver);
    }

    [Fact]
    public void Refund_resolves_the_provider_that_settled_the_sale()
    {
        var (registry, resolver) = Setup("Production", Environments.Production, "stripe", "paypal");

        var provider = RefundProviderResolver.ForPayment(registry, resolver, "paypal");

        Assert.Equal("paypal", provider.ProviderKey); // not the stripe host default
    }

    [Fact]
    public void A_legacy_stripe_payment_still_refunds_through_stripe()
    {
        var (registry, resolver) = Setup("Production", Environments.Production, "stripe", "paypal");

        var provider = RefundProviderResolver.ForPayment(registry, resolver, "stripe");

        Assert.Equal("stripe", provider.ProviderKey);
    }

    [Fact]
    public void LocalMock_refunds_through_the_mock_regardless_of_the_stored_provider()
    {
        // The mode gate overrides the declared provider offline, exactly as it does at authorize.
        var (registry, resolver) = Setup("LocalMock", Environments.Development, "mock", "stripe", "paypal");

        var provider = RefundProviderResolver.ForPayment(registry, resolver, "paypal");

        Assert.Equal("mock", provider.ProviderKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mock")]       // offline dev settled to the seeded stripe accounts
    [InlineData("not-a-psp")]  // never route to an account/adapter that does not exist
    public void An_unknown_or_offline_stored_provider_refunds_through_the_default(string? stored)
    {
        var (registry, resolver) = Setup("Production", Environments.Production, "stripe", "paypal");

        var provider = RefundProviderResolver.ForPayment(registry, resolver, stored);

        Assert.Equal("stripe", provider.ProviderKey);
    }

    [Fact]
    public void An_unregistered_but_known_provider_degrades_to_the_default_instead_of_crashing()
    {
        // "afterpay" is a known ledger provider but has no adapter registered here: the resolver must
        // fall back to the host default so a refund never throws on odd data.
        var (registry, resolver) = Setup("Production", Environments.Production, "stripe");

        var provider = RefundProviderResolver.ForPayment(registry, resolver, "afterpay");

        Assert.Equal("stripe", provider.ProviderKey);
    }

    private sealed class StubProvider(string key) : IPaymentProvider
    {
        public string ProviderKey => key;
        public Task<PaymentResponse> AuthorizeAsync(PaymentRequest r, CancellationToken ct) =>
            Task.FromResult(new PaymentResponse($"pi_{key}_1", "sec", PaymentOutcome.Succeeded));
        public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) => Task.FromResult("cus");
        public Task<SetupIntentResult> CreateSetupIntentAsync(string c, CancellationToken ct) => Task.FromResult(new SetupIntentResult("seti", "sec"));
        public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string id, CancellationToken ct) => Task.FromResult(new SavedPaymentMethodDetails(id, "visa", "4242", 12, 2030));
        public Task<ProviderRefundResult> RefundAsync(string i, long a, string k, CancellationToken ct) => Task.FromResult(new ProviderRefundResult($"{key}_re_1", true));
        public PaymentWebhookEvent? ParseWebhook(string p, string s, IReadOnlyList<string> secrets) => null;
    }
}
