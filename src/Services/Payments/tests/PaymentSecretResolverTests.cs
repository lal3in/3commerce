using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// Per-provider, per-mode secret lookup asserts the credential prefix matches the mode (ADR-0039,
/// fail-closed like DevSecretGuard): a live key under a Sandbox host (or a test key under Production)
/// is refused; a correct prefix passes; LocalMock needs no credential.
/// </summary>
public class PaymentSecretResolverTests
{
    private static PaymentSecretResolver Resolver(string? stripeKey) =>
        new(PaymentTestSupport.Config(("Stripe:SecretKey", stripeKey)));

    [Fact]
    public void Sandbox_accepts_a_test_key()
    {
        var value = Resolver("sk_test_abc").Get("Stripe", PaymentMode.Sandbox, "SecretKey");
        Assert.Equal("sk_test_abc", value);
    }

    [Fact]
    public void Sandbox_refuses_a_live_key()
    {
        var ex = Assert.Throws<PaymentConfigurationException>(
            () => Resolver("sk_live_abc").Get("Stripe", PaymentMode.Sandbox, "SecretKey"));
        Assert.Contains("Sandbox", ex.Message);
    }

    [Fact]
    public void Production_accepts_a_live_key_and_refuses_a_test_key()
    {
        Assert.Equal("sk_live_abc", Resolver("sk_live_abc").Get("Stripe", PaymentMode.Production, "SecretKey"));
        Assert.Throws<PaymentConfigurationException>(() => Resolver("sk_test_abc").Get("Stripe", PaymentMode.Production, "SecretKey"));
    }

    [Fact]
    public void Sandbox_and_production_require_the_key_to_be_present()
    {
        Assert.Throws<PaymentConfigurationException>(() => Resolver(null).Get("Stripe", PaymentMode.Sandbox, "SecretKey"));
        Assert.Throws<PaymentConfigurationException>(() => Resolver("").Get("Stripe", PaymentMode.Production, "SecretKey"));
    }

    [Fact]
    public void LocalMock_needs_no_credential()
    {
        Assert.Equal(string.Empty, Resolver(null).Get("Stripe", PaymentMode.LocalMock, "SecretKey"));
    }
}
