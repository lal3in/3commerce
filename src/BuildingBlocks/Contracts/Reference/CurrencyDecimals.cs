namespace ThreeCommerce.BuildingBlocks.Contracts.Reference;

/// <summary>
/// The ISO 4217 minor-unit exponent (decimal places) for a currency code — the single authority for how
/// many decimals a money amount has when displayed or parsed (currency_3). Amounts are stored as
/// <c>long …Minor</c> (smallest unit); the divisor between minor and major is <c>10^Digits(code)</c>, which
/// is <b>not</b> always 2: JPY/KRW are 0-decimal (¥1500, not ¥15.00) and KWD/BHD are 3-decimal.
/// <para>
/// This mirrors ISO 4217 so store-time (<see cref="Money.ToMinor"/>) and display-time
/// (<see cref="Money.Amount"/>) always agree. The Entity currency registry carries a per-tenant
/// DecimalPlaces too, but display is synchronous (no DB per cell) and real codes follow ISO, so this table
/// is the divisor everywhere. The Admin app keeps a self-contained mirror of this (it references no
/// BuildingBlocks) — keep the two in sync.
/// </para>
/// </summary>
public static class CurrencyDecimals
{
    // ISO 4217 codes whose minor-unit exponent is not the default 2. Everything else is 2.
    private static readonly HashSet<string> Zero = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW",
        "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    private static readonly HashSet<string> Three = new(StringComparer.OrdinalIgnoreCase)
    {
        "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND",
    };

    private static readonly HashSet<string> Four = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLF", "UYW",
    };

    /// <summary>Decimal places for <paramref name="currencyCode"/> (ISO 4217 exponent); default 2.</summary>
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
}
