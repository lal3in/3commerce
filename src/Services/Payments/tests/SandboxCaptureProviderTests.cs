using Microsoft.Extensions.Hosting;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;
using ThreeCommerce.Payments.Infrastructure.Providers.Mock;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// Sandbox ALSO sends the TEST-ONLY payload email; Production NEVER does (pay_3, ADR-0039).
/// The registry wraps the real adapter with <see cref="SandboxCaptureProvider"/> only when the
/// resolved mode is Sandbox; a Production resolution returns the bare adapter, so no capture event
/// can be published there even before the boot guard is considered.
/// </summary>
public class SandboxCaptureProviderTests
{
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

    private sealed class StubProvider(string key) : IPaymentProvider
    {
        public string ProviderKey => key;
        public Task<PaymentResponse> AuthorizeAsync(PaymentRequest r, CancellationToken ct) =>
            Task.FromResult(new PaymentResponse($"pi_{key}_1", "sec", PaymentOutcome.Succeeded));
        public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) => Task.FromResult("cus");
        public Task<SetupIntentResult> CreateSetupIntentAsync(string c, CancellationToken ct) => Task.FromResult(new SetupIntentResult("seti", "sec"));
        public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string id, CancellationToken ct) => Task.FromResult(new SavedPaymentMethodDetails(id, "visa", "4242", 12, 2030));
        public Task<ProviderRefundResult> RefundAsync(string i, long a, string k, CancellationToken ct) => Task.FromResult(new ProviderRefundResult("re_1", true));
        public PaymentWebhookEvent? ParseWebhook(string p, string s, IReadOnlyList<string> secrets) => null;
    }

    private static PaymentRequest Request(PaymentProviderMode accountMode) =>
        new(Guid.NewGuid(), 4990, "EUR", "idem-1", PaymentMethodKind.Card, PaymentTestSupport.Account(accountMode));

    private static (PaymentProviderRegistry Registry, RecordingCapture Capture) Registry(string hostMode, string env)
    {
        var resolver = new PaymentModeResolver(PaymentTestSupport.Config(("Payments:Mode", hostMode)), PaymentTestSupport.Env(env));
        var capture = new RecordingCapture();
        return (new PaymentProviderRegistry([new StubProvider("mock"), new StubProvider("stripe")], resolver, capture), capture);
    }

    [Fact]
    public async Task Sandbox_resolution_wraps_the_real_adapter_and_captures_on_authorize()
    {
        var (registry, capture) = Registry("Sandbox", Environments.Staging);

        var provider = registry.Resolve(PaymentTestSupport.Account(PaymentProviderMode.Test));
        var response = await provider.AuthorizeAsync(Request(PaymentProviderMode.Test), CancellationToken.None);

        Assert.Equal("stripe", provider.ProviderKey);            // still the real adapter's key
        Assert.Equal("pi_stripe_1", response.PaymentIntentId);   // the real adapter did the work
        var e = Assert.Single(capture.Events);
        Assert.Equal("Sandbox", e.Mode);
        Assert.Equal("Authorize", e.Operation);
    }

    [Fact]
    public async Task Sandbox_resolution_captures_on_refund_too()
    {
        var (registry, capture) = Registry("Sandbox", Environments.Staging);
        var provider = registry.Resolve(PaymentTestSupport.Account(PaymentProviderMode.Test));

        var result = await provider.RefundAsync("pi_stripe_1", 500, "refund-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        var e = Assert.Single(capture.Events);
        Assert.Equal("Refund", e.Operation);
        Assert.Equal("Sandbox", e.Mode);
    }

    [Fact]
    public async Task Production_resolution_is_never_wrapped_and_never_captures()
    {
        var (registry, capture) = Registry("Production", Environments.Production);

        var provider = registry.Resolve(PaymentTestSupport.Account(PaymentProviderMode.Live));
        await provider.AuthorizeAsync(Request(PaymentProviderMode.Live), CancellationToken.None);
        await provider.RefundAsync("pi_stripe_1", 500, "refund-1", CancellationToken.None);

        Assert.IsType<StubProvider>(provider); // the bare adapter — no SandboxCaptureProvider wrapper
        Assert.Empty(capture.Events);          // no TEST-ONLY payload event can exist in Production
    }

    [Fact]
    public void Production_host_still_hard_refuses_a_test_account()
    {
        var (registry, _) = Registry("Production", Environments.Production);
        Assert.Throws<PaymentModeException>(() => registry.Resolve(PaymentTestSupport.Account(PaymentProviderMode.Test)));
    }
}
