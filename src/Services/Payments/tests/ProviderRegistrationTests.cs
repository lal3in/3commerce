using Microsoft.Extensions.Hosting;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;
using ThreeCommerce.Payments.Infrastructure.Providers.Afterpay;
using ThreeCommerce.Payments.Infrastructure.Providers.Mock;
using ThreeCommerce.Payments.Infrastructure.Providers.PayPal;
using ThreeCommerce.Payments.Infrastructure.Providers.Polar;
using ThreeCommerce.Payments.Infrastructure.Providers.Stripe;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// pay_4 wiring: every registered adapter resolves through the registry by its lowercase key (the
/// /webhooks/{provider} route path), the wallet method-kind mapping matches ADR-0039 (Apple/Google Pay
/// are methods through the PSP, not providers), and the per-provider base URLs gate sandbox vs
/// production endpoints.
/// </summary>
public class ProviderRegistrationTests
{
    private static PaymentProviderRegistry Registry()
    {
        var config = PaymentTestSupport.Config(("Payments:Mode", "Production"));
        var resolver = new PaymentModeResolver(config, PaymentTestSupport.Env(Environments.Production));
        return new PaymentProviderRegistry(
        [
            new FakePaymentProvider(),
            new StripePaymentProvider(config),
            new PolarPaymentProvider(config),
            new PayPalPaymentProvider(config),
            new AfterpayPaymentProvider(config),
        ], resolver);
    }

    [Theory]
    [InlineData("mock")]
    [InlineData("stripe")]
    [InlineData("polar")]
    [InlineData("paypal")]
    [InlineData("afterpay")]
    public void Every_registered_adapter_resolves_by_its_key(string key)
    {
        Assert.Equal(key, Registry().ResolveByKey(key).ProviderKey);
    }

    [Fact]
    public void Unknown_provider_key_still_throws()
    {
        Assert.Throws<PaymentConfigurationException>(() => Registry().ResolveByKey("klarna"));
    }

    [Theory]
    [InlineData("polar")]
    [InlineData("paypal")]
    [InlineData("afterpay")]
    public void Live_accounts_resolve_the_psp_adapters_in_production(string provider)
    {
        var adapter = Registry().Resolve(PaymentTestSupport.Account(PaymentProviderMode.Live, provider));
        Assert.Equal(provider, adapter.ProviderKey);
    }

    // ------------------------------------------------- method-kind mapping (ADR-0039 wallets-vs-PSP)

    [Theory]
    [InlineData("Stripe", PaymentMethodKind.Card)]
    [InlineData("CreditCard", PaymentMethodKind.Card)]
    [InlineData("ApplePay", PaymentMethodKind.ApplePay)]
    [InlineData("GooglePay", PaymentMethodKind.GooglePay)]
    [InlineData("PayPal", PaymentMethodKind.PayPal)]
    [InlineData("Afterpay", PaymentMethodKind.Afterpay)]
    [InlineData("Polar", PaymentMethodKind.Polar)]
    [InlineData("", PaymentMethodKind.Card)]
    [InlineData(null, PaymentMethodKind.Card)]
    [InlineData("Bitcoin", PaymentMethodKind.Card)]
    public void Checkout_payment_options_map_to_numeric_method_kinds(string? option, PaymentMethodKind expected)
    {
        Assert.Equal(expected, PaymentMethodKindMapper.From(option));
    }

    [Fact]
    public void Wallet_method_kinds_do_not_change_the_resolved_provider()
    {
        // ApplePay/GooglePay are tokenized THROUGH the account's PSP: resolution keys on the
        // account provider only — the method kind rides the PaymentRequest for analytics/receipts.
        var adapter = Registry().Resolve(PaymentTestSupport.Account(PaymentProviderMode.Live, "stripe"));
        Assert.Equal("stripe", adapter.ProviderKey);
        Assert.Equal(PaymentMethodKind.ApplePay, PaymentMethodKindMapper.From("ApplePay"));
    }

    // ---------------------------------------- method → settling-provider routing (ADR-0039 PSP routing)

    [Theory]
    [InlineData(PaymentMethodKind.Card, null)]        // card PSP → default account provider
    [InlineData(PaymentMethodKind.ApplePay, null)]    // wallet through the card PSP
    [InlineData(PaymentMethodKind.GooglePay, null)]   // wallet through the card PSP
    [InlineData(PaymentMethodKind.PayPal, "paypal")]  // standalone PSP → cash.paypal
    [InlineData(PaymentMethodKind.Afterpay, "afterpay")]
    [InlineData(PaymentMethodKind.Polar, "polar")]
    public void Method_kinds_route_to_their_settling_provider(PaymentMethodKind kind, string? expected)
    {
        Assert.Equal(expected, PaymentMethodKindMapper.SettlingProviderFor(kind));
    }

    [Theory]
    [InlineData("paypal")]
    [InlineData("afterpay")]
    [InlineData("polar")]
    public void Account_for_provider_carries_the_routed_key_with_mode_from_host(string provider)
    {
        // Sandbox host → Test account mode; the routed provider is carried verbatim so the registry
        // keys on it (and the persisted Payment.Provider becomes that PSP → cash.{provider}).
        var resolver = new PaymentModeResolver(
            PaymentTestSupport.Config(("Payments:Mode", "Sandbox")), PaymentTestSupport.Env(Environments.Production));
        var account = resolver.AccountForProvider(provider);

        Assert.Equal(provider, account.Provider);
        Assert.Equal(PaymentProviderMode.Test, account.Mode);
        Assert.Equal(PaymentMode.Sandbox, resolver.Resolve(account)); // does not throw for the routed path

        // Production host → Live account mode (mirrors DefaultAccountForHost exactly).
        var prod = new PaymentModeResolver(
            PaymentTestSupport.Config(("Payments:Mode", "Production")), PaymentTestSupport.Env(Environments.Production));
        Assert.Equal(PaymentProviderMode.Live, prod.AccountForProvider(provider).Mode);
    }

    [Fact]
    public void Account_for_provider_resolves_the_mock_adapter_in_localmock()
    {
        // Dev safety: LocalMock overrides the declared provider, so a routed paypal/afterpay/polar
        // account still processes through the mock while Payment.Provider becomes the routed PSP.
        var config = PaymentTestSupport.Config(("Payments:Mode", "LocalMock"));
        var resolver = new PaymentModeResolver(config, PaymentTestSupport.Env(Environments.Development));
        var registry = new PaymentProviderRegistry(
        [
            new FakePaymentProvider(),
            new StripePaymentProvider(config),
            new PayPalPaymentProvider(config),
        ], resolver);
        var account = resolver.AccountForProvider("paypal");

        Assert.Equal(PaymentMode.LocalMock, resolver.Resolve(account)); // never throws in dev
        Assert.Equal("mock", registry.Resolve(account).ProviderKey);
    }

    // -------------------------------------------------------- sandbox/production endpoint gating

    [Theory]
    [InlineData("polar", "https://sandbox-api.polar.sh", "https://api.polar.sh")]
    [InlineData("paypal", "https://api-m.sandbox.paypal.com", "https://api-m.paypal.com")]
    [InlineData("afterpay", "https://global-api-sandbox.afterpay.com", "https://global-api.afterpay.com")]
    public void Base_urls_gate_sandbox_and_production_endpoints(string provider, string sandbox, string production)
    {
        var secrets = new PaymentSecretResolver(PaymentTestSupport.Config());

        Assert.Equal(sandbox, secrets.BaseUrl(provider, PaymentMode.Sandbox));
        Assert.Equal(production, secrets.BaseUrl(provider, PaymentMode.Production));
    }

    [Fact]
    public void Base_url_config_override_wins_and_unknown_provider_throws()
    {
        var secrets = new PaymentSecretResolver(PaymentTestSupport.Config(("Polar:BaseUrl", "https://polar.example.test")));

        Assert.Equal("https://polar.example.test", secrets.BaseUrl("polar", PaymentMode.Sandbox));
        Assert.Throws<PaymentConfigurationException>(() => new PaymentSecretResolver(PaymentTestSupport.Config()).BaseUrl("klarna", PaymentMode.Sandbox));
    }

    // ------------------------------------------------------------ webhook signature-header map

    [Theory]
    [InlineData("stripe", "Stripe-Signature")]
    [InlineData("polar", "Webhook-Signature")]
    [InlineData("paypal", "Paypal-Transmission-Sig")]
    [InlineData("afterpay", "Afterpay-Signature")]
    [InlineData("unknown", "Stripe-Signature")] // fail-closed fallback: verification rejects, not a wrong read
    public void Webhook_signature_header_is_provider_specific(string provider, string header)
    {
        Assert.Equal(header, WebhookSignatureHeaders.For(provider));
    }
}
