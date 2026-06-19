using Microsoft.Extensions.Configuration;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// Configurable home-regime placeholder. With no Tax:HomeCountry configured it returns zero
/// (ADR-0015 launch gate); ship-to outside the home country is zero-rated (ADR-0016 DAP exports).
/// </summary>
public sealed class FlatRateTaxStrategy(IConfiguration configuration) : ITaxStrategy
{
    private readonly string? _homeCountry = NormalizeCountry(configuration["Tax:HomeCountry"]);
    private readonly decimal _rate = decimal.TryParse(configuration["Tax:FlatRate"], out var r) ? r : 0m;

    public long TaxFor(long netMinor, string currency, string? shipCountry)
    {
        if (netMinor <= 0 || _rate <= 0 || string.IsNullOrWhiteSpace(_homeCountry))
        {
            return 0;
        }

        if (!string.Equals(NormalizeCountry(shipCountry), _homeCountry, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        // Banker's rounding (MidpointRounding.ToEven) keeps repeated tax calcs unbiased.
        return (long)Math.Round(netMinor * _rate, MidpointRounding.ToEven);
    }

    private static string? NormalizeCountry(string? country) =>
        string.IsNullOrWhiteSpace(country) ? null : country.Trim().ToUpperInvariant();
}
