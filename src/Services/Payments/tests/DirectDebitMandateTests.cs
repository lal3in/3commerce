using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers.Mock;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// Phase 2 direct-debit: the storefront currency picks the rail (USD→ACH, EUR→SEPA, GBP→Bacs, AUD→BECS,
/// CAD→ACSS; anything else is card-only), and a mandate moves Pending → Active → chargeable off-session.
/// </summary>
public class DirectDebitMandateTests
{
    [Theory]
    [InlineData("USD", DirectDebitScheme.Ach)]
    [InlineData("EUR", DirectDebitScheme.Sepa)]
    [InlineData("GBP", DirectDebitScheme.Bacs)]
    [InlineData("AUD", DirectDebitScheme.Becs)]
    [InlineData("CAD", DirectDebitScheme.Acss)]
    [InlineData("aud", DirectDebitScheme.Becs)] // case-insensitive
    public void Currency_maps_to_its_direct_debit_rail(string currency, DirectDebitScheme expected) =>
        Assert.Equal(expected, DirectDebitSchemes.ForCurrency(currency));

    [Theory]
    [InlineData("JPY")]
    [InlineData("CHF")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unsupported_currency_has_no_rail(string? currency) =>
        Assert.Null(DirectDebitSchemes.ForCurrency(currency));

    [Theory]
    [InlineData(DirectDebitScheme.Ach, "us_bank_account")]
    [InlineData(DirectDebitScheme.Sepa, "sepa_debit")]
    [InlineData(DirectDebitScheme.Bacs, "bacs_debit")]
    [InlineData(DirectDebitScheme.Becs, "au_becs_debit")]
    [InlineData(DirectDebitScheme.Acss, "acss_debit")]
    public void Each_scheme_maps_to_its_stripe_payment_method_type(DirectDebitScheme scheme, string expected) =>
        Assert.Equal(expected, scheme.ToStripePaymentMethodType());

    [Fact]
    public void A_new_mandate_starts_pending_and_is_not_yet_chargeable()
    {
        var mandate = Start();

        Assert.Equal(MandateStatus.Pending, mandate.Status);
        Assert.False(mandate.IsChargeable);
        Assert.Equal("EUR", mandate.Currency);
    }

    [Fact]
    public void Activating_a_mandate_makes_it_chargeable_off_session()
    {
        var mandate = Start();

        mandate.Activate("mandate_1", "pm_sepa_1", DateTimeOffset.UtcNow);

        Assert.Equal(MandateStatus.Active, mandate.Status);
        Assert.True(mandate.IsChargeable);
        Assert.Equal("pm_sepa_1", mandate.ProviderPaymentMethodId);
    }

    [Fact]
    public void A_revoked_mandate_is_no_longer_chargeable()
    {
        var mandate = Start();
        mandate.Activate("mandate_1", "pm_sepa_1", DateTimeOffset.UtcNow);

        mandate.Revoke(DateTimeOffset.UtcNow);

        Assert.Equal(MandateStatus.Revoked, mandate.Status);
        Assert.False(mandate.IsChargeable);
    }

    [Fact]
    public async Task The_mock_provider_supports_mandates_and_confirms_deterministically()
    {
        IDirectDebitProvider provider = new FakePaymentProvider();

        var setup = await provider.CreateMandateSetupAsync("cus_1", DirectDebitScheme.Sepa, "EUR", CancellationToken.None);
        Assert.StartsWith("seti_dd_sepa_", setup.SetupIntentId);

        var confirmation = await provider.GetMandateAsync(setup.SetupIntentId, CancellationToken.None);
        Assert.True(confirmation.Confirmed);
        Assert.NotNull(confirmation.ProviderMandateId);
        Assert.NotNull(confirmation.ProviderPaymentMethodId);
    }

    [Fact]
    public async Task The_mock_provider_registers_a_webhook_endpoint_and_secret()
    {
        IWebhookRegistrationProvider provider = new FakePaymentProvider();

        var registration = await provider.RegisterWebhookAsync("http://localhost:8080/webhooks/mock", CancellationToken.None);

        Assert.StartsWith("we_fake_", registration.EndpointId);
        Assert.StartsWith("whsec_fake_", registration.SigningSecret);
        // Disable is a best-effort no-op for the mock — must not throw.
        await provider.DisableWebhookAsync(registration.EndpointId, CancellationToken.None);
    }

    private static Mandate Start() => Mandate.Start(
        Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "mock",
        DirectDebitScheme.Sepa, "EUR", "seti_dd_sepa_1", DateTimeOffset.UtcNow);
}
