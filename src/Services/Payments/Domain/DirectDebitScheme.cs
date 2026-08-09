namespace ThreeCommerce.Payments.Domain;

/// <summary>The bank direct-debit rails we support, one per storefront settlement currency.</summary>
public enum DirectDebitScheme
{
    Ach = 1,   // USD — US ACH
    Sepa = 2,  // EUR — SEPA Direct Debit
    Bacs = 3,  // GBP — Bacs
    Becs = 4,  // AUD — BECS
    Acss = 5,  // CAD — ACSS (pre-authorized debit)
}

public static class DirectDebitSchemes
{
    /// <summary>
    /// Maps a storefront/settlement currency to its direct-debit rail. The five supported currencies are
    /// USD→ACH, EUR→SEPA, GBP→Bacs, AUD→BECS, CAD→ACSS; any other currency is card-only (null).
    /// </summary>
    public static DirectDebitScheme? ForCurrency(string? currency) => currency?.Trim().ToUpperInvariant() switch
    {
        "USD" => DirectDebitScheme.Ach,
        "EUR" => DirectDebitScheme.Sepa,
        "GBP" => DirectDebitScheme.Bacs,
        "AUD" => DirectDebitScheme.Becs,
        "CAD" => DirectDebitScheme.Acss,
        _ => null,
    };

    /// <summary>The Stripe <c>payment_method_type</c> for each scheme (bank-debit rails).</summary>
    public static string ToStripePaymentMethodType(this DirectDebitScheme scheme) => scheme switch
    {
        DirectDebitScheme.Ach => "us_bank_account",
        DirectDebitScheme.Sepa => "sepa_debit",
        DirectDebitScheme.Bacs => "bacs_debit",
        DirectDebitScheme.Becs => "au_becs_debit",
        DirectDebitScheme.Acss => "acss_debit",
        _ => throw new ArgumentOutOfRangeException(nameof(scheme), scheme, "Unknown direct-debit scheme."),
    };
}
