namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// Tax seam (ADR-0015): no configured home regime means tax = 0. Once a legal entity exists,
/// configure a home country and rate; exports outside the home country are zero-rated.
/// Swap target is Stripe Tax / OSS when requirements are known.
/// </summary>
public interface ITaxStrategy
{
    /// <summary>Tax on a net amount, in minor units. Banker's rounding for fairness/stability.</summary>
    public long TaxFor(long netMinor, string currency, string? shipCountry);
}
