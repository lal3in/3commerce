namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// Tax seam (ADR-0015): v1 is a configurable flat rate placeholder. Swap target is
/// Stripe Tax / OSS once a legal entity and jurisdiction exist.
/// </summary>
public interface ITaxStrategy
{
    /// <summary>Tax on a net amount, in minor units. Banker's rounding for fairness/stability.</summary>
    public long TaxFor(long netMinor, string currency);
}
