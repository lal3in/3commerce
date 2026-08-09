namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// Optional adapter capability for bank direct-debit mandates (ACH/SEPA/BACS/BECS/ACSS). Segregated from
/// <see cref="IPaymentProvider"/> so card-only / merchant-of-record adapters (PayPal, Afterpay, Polar) do
/// not have to implement it — the mandate endpoints resolve the provider and check for this capability.
/// Real provider calls are mode-gated inside the adapter exactly like the rest of the seam; the mock core
/// answers deterministically for offline dev / tests.
/// </summary>
public interface IDirectDebitProvider
{
    /// <summary>
    /// Begins mandate setup for a scheme, returning the provider setup-intent to complete client-side (where
    /// the customer accepts the mandate + supplies bank details — never seen by us). The mandate + payment-
    /// method references land on confirmation, read back via <see cref="GetMandateAsync"/>.
    /// </summary>
    public Task<MandateSetupResult> CreateMandateSetupAsync(string providerCustomerId, DirectDebitScheme scheme, string currency, CancellationToken ct);

    /// <summary>Reads a setup intent's current state so a confirmed mandate can be activated + charged off-session.</summary>
    public Task<MandateConfirmation> GetMandateAsync(string setupIntentId, CancellationToken ct);
}

public sealed record MandateSetupResult(string SetupIntentId, string? ClientSecret);

public sealed record MandateConfirmation(bool Confirmed, string? ProviderMandateId, string? ProviderPaymentMethodId);
