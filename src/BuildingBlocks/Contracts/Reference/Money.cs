using System.Globalization;

namespace ThreeCommerce.BuildingBlocks.Contracts.Reference;

/// <summary>
/// Formats and parses money that is stored as <c>long …Minor</c> (smallest currency unit), honouring each
/// currency's decimal places via <see cref="CurrencyDecimals"/> (currency_3). Use this instead of a hardcoded
/// <c>÷100</c>/<c>×100</c> anywhere money is shown or read from a major-unit string — otherwise a 0-decimal
/// currency (JPY) renders 100× too small and a 3-decimal one (KWD) loses a digit.
/// <para>
/// Rounding is away-from-zero (banker's rounding is wrong for retail money). Formatting is invariant-culture
/// and <b>plain — no thousand separators</b> (e.g. "1234.50"), because this helper feeds machine-readable
/// backend outputs (Google-Merchant product feeds forbid grouping, audit summaries, provider payloads,
/// emails). It is byte-identical to the old <c>:0.00</c>. The Admin dashboards use their own mirror which
/// <i>does</i> group, since that is pure human display. The currency code/symbol is the caller's concern
/// (use <see cref="WithCode"/> for the "1234.50 EUR" form).
/// </para>
/// </summary>
public static class Money
{
    private static readonly decimal[] Pow10 = [1m, 10m, 100m, 1_000m, 10_000m];

    /// <summary>Minor units as a decimal major amount, e.g. 1500 EUR-minor → 15.00m, 1500 JPY-minor → 1500m.</summary>
    public static decimal ToMajor(long minor, string? currencyCode) => minor / Pow10[CurrencyDecimals.Digits(currencyCode)];

    /// <summary>
    /// The amount only, with the currency's decimal places, plain (no grouping, no code/symbol): 1500 EUR →
    /// "15.00", 1500 JPY → "1500", 12345 KWD → "12.345", 362966 AUD → "3629.66".
    /// </summary>
    public static string Amount(long minor, string? currencyCode)
    {
        var digits = CurrencyDecimals.Digits(currencyCode);
        return (minor / Pow10[digits]).ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>The amount followed by the upper-cased ISO code: 1500 EUR → "1,500.00 EUR".</summary>
    public static string WithCode(long minor, string? currencyCode) =>
        $"{Amount(minor, currencyCode)} {(currencyCode ?? string.Empty).Trim().ToUpperInvariant()}";

    /// <summary>
    /// A major-unit decimal to stored minor units, honouring the currency's decimals: 15.00m EUR → 1500,
    /// 1500m JPY → 1500, 12.345m KWD → 12345. Rounds away-from-zero to the currency's precision.
    /// </summary>
    public static long ToMinor(decimal major, string? currencyCode)
    {
        var digits = CurrencyDecimals.Digits(currencyCode);
        return (long)Math.Round(major * Pow10[digits], MidpointRounding.AwayFromZero);
    }
}
