using Microsoft.Extensions.Configuration;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>Flat placeholder rate from config (e.g. Tax:FlatRate=0.19). No jurisdiction logic yet.</summary>
public sealed class FlatRateTaxStrategy(IConfiguration configuration) : ITaxStrategy
{
    private readonly decimal _rate = decimal.TryParse(configuration["Tax:FlatRate"], out var r) ? r : 0m;

    public long TaxFor(long netMinor, string currency)
    {
        // Banker's rounding (MidpointRounding.ToEven) keeps repeated tax calcs unbiased.
        return (long)Math.Round(netMinor * _rate, MidpointRounding.ToEven);
    }
}
