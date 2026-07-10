using Microsoft.Extensions.Hosting;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;
using ThreeCommerce.Payments.Infrastructure.Providers.Mock;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// Registry resolution (ADR-0039): resolves by lowercase provider key; LocalMock overrides the
/// account's declared provider with the mock adapter; unknown keys throw; ResolveDefault reproduces
/// the old startup singleton (mock in dev, the configured provider otherwise).
/// <para>
/// pay_5 regression guard: the account-less/default resolution path (<see cref="PaymentProviderRegistry.ResolveDefault"/>,
/// used by refunds/subscriptions/saved-method endpoints) MUST honour the resolved <see cref="PaymentMode"/>
/// exactly like the account-carrying <see cref="PaymentProviderRegistry.Resolve"/> path — LocalMock through
/// the capturing mock, Sandbox wrapped with capture, Production never capturing. The earlier registry
/// tests only asserted the resolved <c>ProviderKey</c> against non-capturing stubs, so a default path
/// wired to a non-capturing mock (the exact live symptom: TEST-only email silently never sent) passed CI.
/// The tests below drive the REAL <see cref="MockEmailPaymentProvider"/> / <see cref="SandboxCaptureProvider"/>
/// through the default path and assert the capture actually fires.
/// </para>
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

    private sealed class RecordingCapture : IMockPaymentCapture
    {
        public List<MockPaymentCaptured> Events { get; } = [];

        public Task CaptureAuthorizeAsync(PaymentRequest request, string paymentIntentId, PaymentMode mode, CancellationToken ct)
        {
            Events.Add(MockPaymentCapture.BuildAuthorize(request, paymentIntentId, mode, DateTimeOffset.UnixEpoch));
            return Task.CompletedTask;
        }

        public Task CaptureRefundAsync(string providerKey, string paymentIntentId, long amountMinor, string idempotencyKey, PaymentMode mode, CancellationToken ct)
        {
            Events.Add(MockPaymentCapture.BuildRefund(providerKey, paymentIntentId, amountMinor, idempotencyKey, mode, DateTimeOffset.UnixEpoch));
            return Task.CompletedTask;
        }
    }

    private static PaymentProviderRegistry Registry(string hostMode, string env)
    {
        var resolver = new PaymentModeResolver(PaymentTestSupport.Config(("Payments:Mode", hostMode)), PaymentTestSupport.Env(env));
        return new PaymentProviderRegistry([new StubProvider("mock"), new StubProvider("stripe")], resolver);
    }

    /// <summary>
    /// A registry wired exactly like <c>Program.cs</c>: the REAL capturing mock serves the "mock" key and
    /// a stub real adapter serves "stripe", with the capture handed to the registry so Sandbox can wrap.
    /// Returns the resolver too, so the account-less <see cref="PaymentProviderRegistry.ResolveDefault"/>
    /// can be cross-checked against the explicit <c>Resolve(DefaultAccountForHost())</c> account path.
    /// </summary>
    private static (PaymentProviderRegistry Registry, PaymentModeResolver Resolver, RecordingCapture Capture) CapturingRegistry(string hostMode, string env)
    {
        var resolver = new PaymentModeResolver(PaymentTestSupport.Config(("Payments:Mode", hostMode)), PaymentTestSupport.Env(env));
        var capture = new RecordingCapture();
        var registry = new PaymentProviderRegistry(
            [new MockEmailPaymentProvider(capture), new StubProvider("stripe")], resolver, capture);
        return (registry, resolver, capture);
    }

    private static PaymentRequest Request() =>
        new(
            OrderId: Guid.Parse("3f2a0000-0000-0000-0000-000000000c91"),
            AmountMinor: 4990,
            Currency: "EUR",
            IdempotencyKey: "ord-3f2a-1",
            MethodKind: PaymentMethodKind.Card,
            Account: PaymentTestSupport.Account(PaymentProviderMode.Test));

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

    // ------------------------------------------------------------------------------------------------
    // pay_5: the account-less/default resolution path must capture in LocalMock exactly like the
    // account-carrying path — the TEST-only mock-payment email rides this capture, so a default path
    // that skips it means checkout/refund silently sends no email at all.

    [Fact]
    public async Task ResolveDefault_authorize_goes_through_the_capturing_mock_in_localmock()
    {
        var (registry, _, capture) = CapturingRegistry("LocalMock", Environments.Development);

        var response = await registry.ResolveDefault().AuthorizeAsync(Request(), CancellationToken.None);

        var e = Assert.Single(capture.Events);
        Assert.Equal("Authorize", e.Operation);
        Assert.Equal("LocalMock", e.Mode);
        Assert.Equal(response.PaymentIntentId, e.PaymentIntentId);
        Assert.StartsWith("pi_fake_", response.PaymentIntentId); // simulate/ledger funnel unchanged
    }

    [Fact]
    public async Task ResolveDefault_refund_goes_through_the_capturing_mock_in_localmock()
    {
        var (registry, _, capture) = CapturingRegistry("LocalMock", Environments.Development);

        var result = await registry.ResolveDefault().RefundAsync("pi_fake_abc", 500, "refund-key-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        var e = Assert.Single(capture.Events);
        Assert.Equal("Refund", e.Operation);
        Assert.Equal("LocalMock", e.Mode);
        Assert.Equal(500, e.AmountMinor);
    }

    [Fact]
    public async Task Account_less_default_and_explicit_default_account_resolve_the_same_capturing_path()
    {
        var (registry, resolver, capture) = CapturingRegistry("LocalMock", Environments.Development);

        // ResolveDefault() is exactly Resolve(DefaultAccountForHost()); both refund seams must capture.
        await registry.ResolveDefault().RefundAsync("pi_fake_1", 100, "k1", CancellationToken.None);
        await registry.Resolve(resolver.DefaultAccountForHost()).RefundAsync("pi_fake_2", 100, "k2", CancellationToken.None);

        Assert.Equal(2, capture.Events.Count);
        Assert.All(capture.Events, e => Assert.Equal("LocalMock", e.Mode));
    }

    [Fact]
    public async Task ResolveDefault_wraps_the_real_adapter_with_capture_in_sandbox()
    {
        var (registry, _, capture) = CapturingRegistry("Sandbox", Environments.Production);

        var response = await registry.ResolveDefault().AuthorizeAsync(Request(), CancellationToken.None);

        Assert.Equal("pi", response.PaymentIntentId); // the real (stub) adapter did the authorize
        var e = Assert.Single(capture.Events);
        Assert.Equal("Sandbox", e.Mode);
    }

    [Fact]
    public async Task ResolveDefault_never_captures_in_production()
    {
        var (registry, _, capture) = CapturingRegistry("Production", Environments.Production);

        var provider = registry.ResolveDefault();
        await provider.AuthorizeAsync(Request(), CancellationToken.None);
        await provider.RefundAsync("pi", 500, "k", CancellationToken.None);

        Assert.Equal("stripe", provider.ProviderKey); // the configured live adapter, unwrapped
        Assert.Empty(capture.Events);
    }
}
