using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers.Mock;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// The LocalMock adapter (pay_3, ADR-0039): all six MockScenario values map to the right
/// PaymentOutcome/PaymentErrorCode; every authorize AND refund publishes a MockPaymentCaptured
/// event whose payload is redacted (never wallet token/PAN); the deterministic FakePaymentProvider
/// core (pi_fake_… intent ids, customers, setup-intents) is preserved so the simulate/ledger funnel
/// is unchanged.
/// </summary>
public class MockEmailPaymentProviderTests
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

    private static PaymentRequest Request(MockScenario? scenario, string? walletToken = null, string? providerPaymentMethodId = null) =>
        new(
            OrderId: Guid.Parse("3f2a0000-0000-0000-0000-000000000c91"),
            AmountMinor: 4990,
            Currency: "EUR",
            IdempotencyKey: "ord-3f2a-1",
            MethodKind: PaymentMethodKind.Card,
            Account: PaymentTestSupport.Account(PaymentProviderMode.Test),
            ProviderPaymentMethodId: providerPaymentMethodId,
            WalletToken: walletToken,
            Scenario: scenario);

    private static (MockEmailPaymentProvider Provider, RecordingCapture Capture) Sut()
    {
        var capture = new RecordingCapture();
        return (new MockEmailPaymentProvider(capture), capture);
    }

    [Theory]
    [InlineData(MockScenario.Success, PaymentOutcome.Succeeded, null)]
    [InlineData(MockScenario.Failure, PaymentOutcome.Failed, PaymentErrorCode.ProcessingError)]
    [InlineData(MockScenario.DeclinedCard, PaymentOutcome.Failed, PaymentErrorCode.CardDeclined)]
    [InlineData(MockScenario.ExpiredCard, PaymentOutcome.Failed, PaymentErrorCode.ExpiredCard)]
    [InlineData(MockScenario.Requires3ds, PaymentOutcome.RequiresAction, PaymentErrorCode.AuthenticationRequired)]
    [InlineData(MockScenario.Cancelled, PaymentOutcome.Cancelled, null)]
    public async Task Every_scenario_maps_to_the_right_outcome_and_error(
        MockScenario scenario, PaymentOutcome expectedOutcome, PaymentErrorCode? expectedError)
    {
        var (provider, _) = Sut();

        var response = await provider.AuthorizeAsync(Request(scenario), CancellationToken.None);

        Assert.Equal(expectedOutcome, response.Outcome);
        Assert.Equal(expectedError, response.Error?.Code);
    }

    [Fact]
    public async Task No_scenario_defaults_to_success_with_the_deterministic_fake_intent()
    {
        var (provider, _) = Sut();

        var response = await provider.AuthorizeAsync(Request(scenario: null), CancellationToken.None);

        Assert.Equal(PaymentOutcome.Succeeded, response.Outcome);
        Assert.StartsWith("pi_fake_", response.PaymentIntentId); // simulate/ledger funnel unchanged
        Assert.NotNull(response.ClientSecret);
    }

    [Fact]
    public async Task Requires3ds_keeps_the_client_secret_so_the_client_can_confirm()
    {
        var (provider, _) = Sut();

        var response = await provider.AuthorizeAsync(Request(MockScenario.Requires3ds), CancellationToken.None);

        Assert.NotNull(response.ClientSecret);
        Assert.True(response.Error!.Retryable);
    }

    [Theory]
    [InlineData(MockScenario.Success)]
    [InlineData(MockScenario.Failure)]
    [InlineData(MockScenario.DeclinedCard)]
    [InlineData(MockScenario.ExpiredCard)]
    [InlineData(MockScenario.Requires3ds)]
    [InlineData(MockScenario.Cancelled)]
    public async Task Every_authorize_publishes_one_capture_event_labelled_with_the_scenario(MockScenario scenario)
    {
        var (provider, capture) = Sut();

        var response = await provider.AuthorizeAsync(Request(scenario), CancellationToken.None);

        var e = Assert.Single(capture.Events);
        Assert.Equal("Authorize", e.Operation);
        Assert.Equal(scenario.ToString(), e.Scenario);
        Assert.Equal("LocalMock", e.Mode);
        Assert.Equal(response.PaymentIntentId, e.PaymentIntentId);
        Assert.Equal(4990, e.AmountMinor);
        Assert.Equal("EUR", e.Currency);
    }

    [Fact]
    public async Task Capture_payload_is_redacted_no_wallet_token_and_masked_method_ref()
    {
        var (provider, capture) = Sut();

        await provider.AuthorizeAsync(
            Request(MockScenario.Success, walletToken: "GPAY_TOKEN_BLOB_123", providerPaymentMethodId: "pm_secret_ref_4242"),
            CancellationToken.None);

        var e = Assert.Single(capture.Events);
        Assert.DoesNotContain("GPAY_TOKEN_BLOB_123", e.RedactedPayloadJson);
        Assert.DoesNotContain("walletToken", e.RedactedPayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pm_secret_ref_4242", e.RedactedPayloadJson);
        Assert.Contains("pm_redacted_4242", e.RedactedPayloadJson);
    }

    [Fact]
    public async Task Refund_succeeds_and_publishes_a_redacted_refund_capture()
    {
        var (provider, capture) = Sut();

        var result = await provider.RefundAsync("pi_fake_abc", 500, "refund-key-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        var e = Assert.Single(capture.Events);
        Assert.Equal("Refund", e.Operation);
        Assert.Equal("mock", e.Provider);
        Assert.Equal("LocalMock", e.Mode);
        Assert.Equal(500, e.AmountMinor);
        Assert.Contains("pi_fake_abc", e.RedactedPayloadJson);
    }

    [Fact]
    public async Task Deterministic_core_is_preserved_for_customers_and_setup_intents()
    {
        var (provider, _) = Sut();
        var userId = Guid.NewGuid();

        Assert.Equal("mock", provider.ProviderKey);
        Assert.Equal($"cus_fake_{userId:N}", await provider.CreateCustomerAsync(userId, "a@b.c", CancellationToken.None));
        var setup = await provider.CreateSetupIntentAsync("cus_fake_x", CancellationToken.None);
        Assert.StartsWith("seti_fake_", setup.SetupIntentId);
        Assert.Null(provider.ParseWebhook("{}", "sig", []));
    }
}
