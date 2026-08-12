using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Tests;

public class CurrencyTests
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_normalises_the_code_and_defaults_enabled()
    {
        var c = Currency.Create(Tenant, " jpy ", "Japanese Yen", "¥", 0, Now);
        Assert.Equal("JPY", c.Code);
        Assert.Equal("Japanese Yen", c.Name);
        Assert.Equal("¥", c.Symbol);
        Assert.Equal(0, c.DecimalPlaces);
        Assert.True(c.Enabled);
    }

    [Theory]
    [InlineData("US")]      // too short
    [InlineData("USDD")]    // too long
    [InlineData("US1")]     // not alphabetic
    [InlineData("")]
    public void Create_rejects_a_non_iso_code(string code) =>
        Assert.Throws<DomainRuleException>(() => Currency.Create(Tenant, code, "X", "$", 2, Now));

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void Create_rejects_decimals_out_of_range(int decimals) =>
        Assert.Throws<DomainRuleException>(() => Currency.Create(Tenant, "USD", "US Dollar", "$", decimals, Now));

    [Fact]
    public void Create_requires_a_name() =>
        Assert.Throws<DomainRuleException>(() => Currency.Create(Tenant, "USD", "  ", "$", 2, Now));

    [Fact]
    public void Update_changes_metadata_but_not_the_code()
    {
        var c = Currency.Create(Tenant, "USD", "US Dollar", "$", 2, Now);
        c.UpdateDetails("United States Dollar", "US$", 2, Now.AddMinutes(1));
        Assert.Equal("USD", c.Code); // immutable
        Assert.Equal("United States Dollar", c.Name);
        Assert.Equal("US$", c.Symbol);
    }

    [Fact]
    public void Disable_then_enable_flips_the_flag_forward_only()
    {
        var c = Currency.Create(Tenant, "USD", "US Dollar", "$", 2, Now);
        c.Disable(Now.AddMinutes(1));
        Assert.False(c.Enabled);
        c.Enable(Now.AddMinutes(2));
        Assert.True(c.Enabled);
    }
}
