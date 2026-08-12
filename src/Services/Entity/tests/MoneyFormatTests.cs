using ThreeCommerce.BuildingBlocks.Contracts.Reference;

namespace ThreeCommerce.Entity.Tests;

/// <summary>
/// The shared money formatter/parser honours each currency's ISO 4217 decimal places (currency_3): a
/// 2-decimal code divides by 100, a 0-decimal code (JPY) does not divide, a 3-decimal code (KWD) by 1000.
/// Store-time (<see cref="Money.ToMinor"/>) and display-time (<see cref="Money.Amount"/>) must agree.
/// </summary>
public class MoneyFormatTests
{
    [Theory]
    [InlineData("USD", 2)]
    [InlineData("eur", 2)] // case-insensitive
    [InlineData("JPY", 0)]
    [InlineData("KRW", 0)]
    [InlineData("KWD", 3)]
    [InlineData("BHD", 3)]
    [InlineData("CLF", 4)]
    [InlineData("ZZZ", 2)] // unknown → default 2
    [InlineData("", 2)]
    [InlineData(null, 2)]
    public void Digits_follows_iso_4217(string? code, int expected) =>
        Assert.Equal(expected, CurrencyDecimals.Digits(code));

    [Theory]
    [InlineData(1500, "EUR", "15.00")]
    [InlineData(1500, "JPY", "1500")] // 0 decimals: minor == whole yen, no grouping (machine-safe)
    [InlineData(12345, "KWD", "12.345")] // 3 decimals
    [InlineData(123456, "USD", "1234.56")] // plain — no thousand separators
    [InlineData(0, "JPY", "0")]
    public void Amount_renders_currency_decimals(long minor, string code, string expected) =>
        Assert.Equal(expected, Money.Amount(minor, code));

    [Theory]
    [InlineData(1500, "EUR", "15.00 EUR")]
    [InlineData(1500, "jpy", "1500 JPY")]
    public void WithCode_appends_the_upper_cased_code(long minor, string code, string expected) =>
        Assert.Equal(expected, Money.WithCode(minor, code));

    [Theory]
    [InlineData(15.00, "EUR", 1500)]
    [InlineData(1500, "JPY", 1500)] // no division for 0-decimal
    [InlineData(12.345, "KWD", 12345)]
    [InlineData(0.005, "USD", 1)] // away-from-zero rounding: 0.005 → 1 cent
    public void ToMinor_scales_by_currency_decimals(decimal major, string code, long expected) =>
        Assert.Equal(expected, Money.ToMinor(major, code));

    [Theory]
    [InlineData("EUR")]
    [InlineData("JPY")]
    [InlineData("KWD")]
    public void ToMinor_round_trips_with_ToMajor(string code)
    {
        const long minor = 1_234_500;
        Assert.Equal(minor, Money.ToMinor(Money.ToMajor(minor, code), code));
    }
}
