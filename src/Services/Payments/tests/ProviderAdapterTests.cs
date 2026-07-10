using System.Security.Cryptography;
using System.Text;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;
using ThreeCommerce.Payments.Infrastructure.Providers.Afterpay;
using ThreeCommerce.Payments.Infrastructure.Providers.PayPal;
using ThreeCommerce.Payments.Infrastructure.Providers.Polar;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// pay_4 PSP adapters (Polar/PayPal/Afterpay, ADR-0039): sandbox-ready skeletons behind the seam.
/// Covers authorize (sandbox creds required — refuses without them), refund, and webhook parse shape
/// against recorded sandbox-style fixtures signed with the skeleton HMAC scheme (rotation-safe over
/// the def_2 secret set). Webhook events must normalize into the provider-agnostic
/// <see cref="PaymentWebhookEvent"/> that feeds the single PaymentEventProcessor.
/// </summary>
public class ProviderAdapterTests
{
    private static readonly Guid OrderId = Guid.Parse("3f2a0000-0000-0000-0000-000000000c91");

    private static PaymentRequest Request(PaymentProviderMode mode, string provider) => new(
        OrderId, 4990, "EUR", "idem-1", PaymentMethodKind.Card, PaymentTestSupport.Account(mode, provider));

    /// <summary>Stripe-style skeleton signature: HMAC-SHA256 over "{timestamp}.{payload}", "t=…,v1=…".</summary>
    private static string Sign(string secret, string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}")));
        return $"t={timestamp},v1={signature}";
    }

    // ---------------------------------------------------------------- Polar

    // Recorded sandbox fixture shape: Polar order.paid webhook (sandbox-api.polar.sh).
    private const string PolarPaid =
        """{"id":"evt_polar_1","type":"order.paid","data":{"id":"polar_order_1","amount":4990,"currency":"eur"}}""";

    private const string PolarFailed =
        """{"id":"evt_polar_2","type":"order.payment_failed","data":{"id":"polar_order_2","amount":4990,"failure_reason":"card_declined"}}""";

    [Fact]
    public async Task Polar_authorizes_in_sandbox_with_test_credentials()
    {
        var provider = new PolarPaymentProvider(PaymentTestSupport.Config(("Polar:AccessToken", "polar_at_test")));

        var response = await provider.AuthorizeAsync(Request(PaymentProviderMode.Test, "polar"), CancellationToken.None);

        Assert.StartsWith("polar_test_", response.PaymentIntentId);
        Assert.Equal(PaymentOutcome.RequiresAction, response.Outcome); // hosted checkout: webhook owns the truth
        Assert.NotNull(response.ClientSecret);
    }

    [Fact]
    public async Task Polar_refuses_to_authorize_without_credentials()
    {
        var provider = new PolarPaymentProvider(PaymentTestSupport.Config());

        await Assert.ThrowsAsync<PaymentConfigurationException>(
            () => provider.AuthorizeAsync(Request(PaymentProviderMode.Test, "polar"), CancellationToken.None));
    }

    [Fact]
    public async Task Polar_refund_returns_a_provider_scoped_refund_id()
    {
        var result = await new PolarPaymentProvider(PaymentTestSupport.Config())
            .RefundAsync("polar_order_1", 1000, "refund-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.StartsWith("polar_re_", result.RefundId);
    }

    [Fact]
    public void Polar_parses_a_signed_paid_webhook_into_the_normalized_shape()
    {
        var ev = new PolarPaymentProvider(PaymentTestSupport.Config())
            .ParseWebhook(PolarPaid, Sign("whsec_polar", PolarPaid), ["whsec_polar"]);

        Assert.NotNull(ev);
        Assert.Equal("evt_polar_1", ev.EventId);
        Assert.Equal(PaymentWebhookKind.PaymentSucceeded, ev.Kind);
        Assert.Equal("polar_order_1", ev.PaymentIntentId);
        Assert.Equal(4990, ev.AmountMinor);
        Assert.Null(ev.FailureReason);
    }

    [Fact]
    public void Polar_parses_a_failed_webhook_with_its_failure_reason()
    {
        var ev = new PolarPaymentProvider(PaymentTestSupport.Config())
            .ParseWebhook(PolarFailed, Sign("whsec_polar", PolarFailed), ["whsec_polar"]);

        Assert.NotNull(ev);
        Assert.Equal(PaymentWebhookKind.PaymentFailed, ev.Kind);
        Assert.Equal("card_declined", ev.FailureReason);
    }

    [Fact]
    public void Polar_rejects_a_bad_signature_and_unknown_event_types()
    {
        var provider = new PolarPaymentProvider(PaymentTestSupport.Config());

        Assert.Null(provider.ParseWebhook(PolarPaid, Sign("whsec_wrong", PolarPaid), ["whsec_polar"]));
        Assert.Null(provider.ParseWebhook(PolarPaid, Sign("whsec_polar", PolarPaid), []));

        var benign = """{"id":"evt_polar_3","type":"order.created","data":{"id":"polar_order_3"}}""";
        Assert.Null(provider.ParseWebhook(benign, Sign("whsec_polar", benign), ["whsec_polar"]));
    }

    // --------------------------------------------------------------- PayPal

    // Recorded sandbox fixture shape: PayPal Webhooks v1 capture event (api-m.sandbox.paypal.com).
    private const string PayPalCompleted =
        """{"id":"WH-58D329510W468432D-8HN650336L201105X","event_type":"PAYMENT.CAPTURE.COMPLETED","resource":{"id":"42311647XV020574X","amount":{"currency_code":"EUR","value":"49.90"}}}""";

    private const string PayPalDenied =
        """{"id":"WH-4SW78779LY2325805-07E03580SX1414828","event_type":"PAYMENT.CAPTURE.DENIED","resource":{"id":"7NW873794T343360M","amount":{"currency_code":"EUR","value":"49.90"},"status_details":{"reason":"DECLINED"}}}""";

    [Fact]
    public async Task PayPal_authorizes_in_sandbox_with_test_credentials()
    {
        var provider = new PayPalPaymentProvider(PaymentTestSupport.Config(
            ("PayPal:ClientId", "sb-client"), ("PayPal:Secret", "sb-secret")));

        var response = await provider.AuthorizeAsync(Request(PaymentProviderMode.Test, "paypal"), CancellationToken.None);

        Assert.StartsWith("paypal_test_", response.PaymentIntentId);
        Assert.Equal(PaymentOutcome.RequiresAction, response.Outcome); // shopper approves on PayPal
    }

    [Fact]
    public async Task PayPal_refuses_to_authorize_without_credentials()
    {
        // Client id alone is not enough — the secret is also required.
        var provider = new PayPalPaymentProvider(PaymentTestSupport.Config(("PayPal:ClientId", "sb-client")));

        await Assert.ThrowsAsync<PaymentConfigurationException>(
            () => provider.AuthorizeAsync(Request(PaymentProviderMode.Test, "paypal"), CancellationToken.None));
    }

    [Fact]
    public async Task PayPal_refund_returns_a_provider_scoped_refund_id()
    {
        var result = await new PayPalPaymentProvider(PaymentTestSupport.Config())
            .RefundAsync("42311647XV020574X", 1000, "refund-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.StartsWith("paypal_re_", result.RefundId);
    }

    [Fact]
    public void PayPal_parses_a_signed_capture_webhook_and_converts_the_decimal_amount_to_minor()
    {
        var ev = new PayPalPaymentProvider(PaymentTestSupport.Config())
            .ParseWebhook(PayPalCompleted, Sign("whsec_paypal", PayPalCompleted), ["whsec_paypal"]);

        Assert.NotNull(ev);
        Assert.Equal("WH-58D329510W468432D-8HN650336L201105X", ev.EventId);
        Assert.Equal(PaymentWebhookKind.PaymentSucceeded, ev.Kind);
        Assert.Equal("42311647XV020574X", ev.PaymentIntentId);
        Assert.Equal(4990, ev.AmountMinor); // "49.90" → 4990 minor units
    }

    [Fact]
    public void PayPal_parses_a_denied_capture_with_its_reason()
    {
        var ev = new PayPalPaymentProvider(PaymentTestSupport.Config())
            .ParseWebhook(PayPalDenied, Sign("whsec_paypal", PayPalDenied), ["whsec_paypal"]);

        Assert.NotNull(ev);
        Assert.Equal(PaymentWebhookKind.PaymentFailed, ev.Kind);
        Assert.Equal("DECLINED", ev.FailureReason);
    }

    [Fact]
    public void PayPal_rejects_a_bad_signature_and_unknown_event_types()
    {
        var provider = new PayPalPaymentProvider(PaymentTestSupport.Config());

        Assert.Null(provider.ParseWebhook(PayPalCompleted, Sign("whsec_wrong", PayPalCompleted), ["whsec_paypal"]));

        var benign = """{"id":"WH-1","event_type":"CHECKOUT.ORDER.APPROVED","resource":{"id":"5O190127TN364715T"}}""";
        Assert.Null(provider.ParseWebhook(benign, Sign("whsec_paypal", benign), ["whsec_paypal"]));
    }

    // -------------------------------------------------------------- Afterpay

    // Recorded sandbox fixture shape: Afterpay payment event (global-api-sandbox.afterpay.com).
    private const string AfterpayApproved =
        """{"id":"evt_ap_1","type":"payment.approved","data":{"id":"100101782114","amount":{"amount":"49.90","currency":"EUR"}}}""";

    private const string AfterpayDeclined =
        """{"id":"evt_ap_2","type":"payment.declined","data":{"id":"100101782115","amount":{"amount":"49.90","currency":"EUR"},"reason":"INSUFFICIENT_FUNDS"}}""";

    [Fact]
    public async Task Afterpay_authorizes_in_sandbox_with_test_credentials()
    {
        var provider = new AfterpayPaymentProvider(PaymentTestSupport.Config(
            ("Afterpay:MerchantId", "400101"), ("Afterpay:SecretKey", "ap-secret")));

        var response = await provider.AuthorizeAsync(Request(PaymentProviderMode.Test, "afterpay"), CancellationToken.None);

        Assert.StartsWith("afterpay_test_", response.PaymentIntentId);
        Assert.Equal(PaymentOutcome.RequiresAction, response.Outcome); // shopper completes the BNPL flow
    }

    [Fact]
    public async Task Afterpay_refuses_to_authorize_without_credentials()
    {
        var provider = new AfterpayPaymentProvider(PaymentTestSupport.Config());

        await Assert.ThrowsAsync<PaymentConfigurationException>(
            () => provider.AuthorizeAsync(Request(PaymentProviderMode.Test, "afterpay"), CancellationToken.None));
    }

    [Fact]
    public async Task Afterpay_refund_returns_a_provider_scoped_refund_id()
    {
        var result = await new AfterpayPaymentProvider(PaymentTestSupport.Config())
            .RefundAsync("100101782114", 1000, "refund-1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.StartsWith("afterpay_re_", result.RefundId);
    }

    [Fact]
    public void Afterpay_parses_a_signed_approved_webhook_and_converts_the_decimal_amount_to_minor()
    {
        var ev = new AfterpayPaymentProvider(PaymentTestSupport.Config())
            .ParseWebhook(AfterpayApproved, Sign("whsec_afterpay", AfterpayApproved), ["whsec_afterpay"]);

        Assert.NotNull(ev);
        Assert.Equal("evt_ap_1", ev.EventId);
        Assert.Equal(PaymentWebhookKind.PaymentSucceeded, ev.Kind);
        Assert.Equal("100101782114", ev.PaymentIntentId);
        Assert.Equal(4990, ev.AmountMinor);
    }

    [Fact]
    public void Afterpay_parses_a_declined_webhook_with_its_reason()
    {
        var ev = new AfterpayPaymentProvider(PaymentTestSupport.Config())
            .ParseWebhook(AfterpayDeclined, Sign("whsec_afterpay", AfterpayDeclined), ["whsec_afterpay"]);

        Assert.NotNull(ev);
        Assert.Equal(PaymentWebhookKind.PaymentFailed, ev.Kind);
        Assert.Equal("INSUFFICIENT_FUNDS", ev.FailureReason);
    }

    [Fact]
    public void Afterpay_rejects_a_bad_signature()
    {
        Assert.Null(new AfterpayPaymentProvider(PaymentTestSupport.Config())
            .ParseWebhook(AfterpayApproved, Sign("whsec_wrong", AfterpayApproved), ["whsec_afterpay"]));
    }

    // ---------------------------------------------------- Webhook rotation + saved-method posture

    [Fact]
    public void Skeleton_webhooks_verify_against_any_active_secret_newest_first()
    {
        // Rotation-safety (def_2): a payload still signed with the OLD secret verifies mid-rotation.
        var ev = new PolarPaymentProvider(PaymentTestSupport.Config())
            .ParseWebhook(PolarPaid, Sign("whsec_old", PolarPaid), ["whsec_new", "whsec_old"]);

        Assert.NotNull(ev);
    }

    [Fact]
    public async Task Psp_adapters_do_not_pretend_to_support_stripe_style_saved_methods()
    {
        // Saved cards / off-session customers stay on the Stripe provider (ADR-0039).
        IPaymentProvider[] adapters =
        [
            new PolarPaymentProvider(PaymentTestSupport.Config()),
            new PayPalPaymentProvider(PaymentTestSupport.Config()),
            new AfterpayPaymentProvider(PaymentTestSupport.Config()),
        ];

        foreach (var adapter in adapters)
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => adapter.CreateCustomerAsync(Guid.NewGuid(), "s@x.y", CancellationToken.None));
            await Assert.ThrowsAsync<NotSupportedException>(() => adapter.CreateSetupIntentAsync("cus_1", CancellationToken.None));
            await Assert.ThrowsAsync<NotSupportedException>(() => adapter.GetPaymentMethodAsync("pm_1", CancellationToken.None));
        }
    }
}
