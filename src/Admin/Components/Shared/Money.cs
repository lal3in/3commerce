using System.Globalization;

namespace ThreeCommerce.Admin.Components.Shared;

/// <summary>
/// Formats money stored as <c>long …Minor</c> (smallest unit) honouring each currency's decimal places
/// (currency_3), so JPY shows ¥1,500 (0 dp) not ¥15.00 and KWD shows 3 dp. The Admin app references no
/// BuildingBlocks, so this mirrors <c>ThreeCommerce.BuildingBlocks.Contracts.Reference.Money</c> /
/// <c>CurrencyDecimals</c> — keep them in sync. Every money render in the Admin pages goes through
/// <see cref="Amount"/>/<see cref="WithCode"/> instead of a hardcoded <c>÷100</c>.
/// </summary>
public static class Money
{
    private static readonly decimal[] Pow10 = [1m, 10m, 100m, 1_000m, 10_000m];

    // ISO 4217 minor-unit exponents that are not the default 2 (mirror of BuildingBlocks CurrencyDecimals).
    private static readonly HashSet<string> Zero = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW",
        "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    private static readonly HashSet<string> Three = new(StringComparer.OrdinalIgnoreCase)
    {
        "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND",
    };

    private static readonly HashSet<string> Four = new(StringComparer.OrdinalIgnoreCase) { "CLF", "UYW" };

    /// <summary>Decimal places for a currency code (ISO 4217 exponent); default 2.</summary>
    public static int Digits(string? currencyCode)
    {
        var code = (currencyCode ?? string.Empty).Trim();
        if (Zero.Contains(code))
        {
            return 0;
        }

        if (Three.Contains(code))
        {
            return 3;
        }

        return Four.Contains(code) ? 4 : 2;
    }

    /// <summary>The amount only, with the currency's decimals + grouping: 1500 EUR → "1,500.00", 1500 JPY → "1,500".</summary>
    public static string Amount(long minor, string? currencyCode)
    {
        var digits = Digits(currencyCode);
        return (minor / Pow10[digits]).ToString("N" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>The amount followed by the upper-cased ISO code: 1500 EUR → "1,500.00 EUR".</summary>
    public static string WithCode(long minor, string? currencyCode) =>
        $"{Amount(minor, currencyCode)} {(currencyCode ?? string.Empty).Trim().ToUpperInvariant()}";
}
