using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// mem_2: each saved payment method carries its own billing address, which checkout uses as the order's bill-to when
/// the shopper pays with that card. Normalized and bounded like the address book; refusals name the part at fault;
/// set and cleared only on an active method; prefill data only — the charge snapshot never carries it.
/// </summary>
public class CardBillingAddressTests
{
    [Fact]
    public void An_address_is_trimmed_with_an_upper_case_country_and_no_blank_region()
    {
        var address = CardBillingAddress.Create(" Ada Buyer ", " 9 Ledger Road ", " Munich ", "  ", " 80331 ", " de ");

        Assert.Equal(new CardBillingAddress("Ada Buyer", "9 Ledger Road", "Munich", null, "80331", "DE"), address);
    }

    [Theory]
    [InlineData(null, "1 St", "City", "3000", "AU", CardBillingAddress.NameField)]
    [InlineData("Ada", " ", "City", "3000", "AU", CardBillingAddress.Line1Field)]
    [InlineData("Ada", "1 St", "", "3000", "AU", CardBillingAddress.CityField)]
    [InlineData("Ada", "1 St", "City", null, "AU", CardBillingAddress.PostcodeField)]
    [InlineData("Ada", "1 St", "City", "3000", "AUS", CardBillingAddress.CountryField)]
    [InlineData("Ada", "1 St", "City", "3000", "A1", CardBillingAddress.CountryField)]
    [InlineData("Ada", "1 St", "City", "3000", null, CardBillingAddress.CountryField)]
    public void A_refusal_names_the_part_at_fault(string? name, string? line1, string? city, string? postcode, string? country, string field)
    {
        var ex = Assert.Throws<CardBillingAddressException>(() => CardBillingAddress.Create(name, line1, city, null, postcode, country));

        Assert.Equal(field, ex.Field);
    }

    [Fact]
    public void Oversize_parts_are_refused_not_truncated()
    {
        Assert.Equal(CardBillingAddress.NameField, Assert.Throws<CardBillingAddressException>(() =>
            CardBillingAddress.Create(new string('a', CardBillingAddress.NameMaxLength + 1), "1 St", "City", null, "3000", "AU")).Field);
        Assert.Equal(CardBillingAddress.RegionField, Assert.Throws<CardBillingAddressException>(() =>
            CardBillingAddress.Create("Ada", "1 St", "City", new string('r', CardBillingAddress.RegionMaxLength + 1), "3000", "AU")).Field);
        Assert.Equal(CardBillingAddress.PostcodeField, Assert.Throws<CardBillingAddressException>(() =>
            CardBillingAddress.Create("Ada", "1 St", "City", null, new string('9', CardBillingAddress.PostcodeMaxLength + 1), "AU")).Field);
    }

    [Fact]
    public void A_method_sets_and_clears_its_billing_address_at_stored_precision()
    {
        var method = NewMethod();
        var address = CardBillingAddress.Create("Ada Buyer", "1 Test Street", "Melbourne", "VIC", "3000", "AU");
        // A Linux clock fills the 7th fractional digit; Postgres keeps microseconds (engineering-gotchas).
        var at = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero).AddTicks(1_755_249);

        method.SetBillingAddress(address, at);

        Assert.Equal(address, method.BillingAddress);
        Assert.Equal(StoredTime.Of(at), method.UpdatedAt);
        Assert.Equal(0, method.UpdatedAt.Ticks % TimeSpan.TicksPerMicrosecond);

        method.SetBillingAddress(null, at);

        Assert.Null(method.BillingAddress);
        Assert.Null(method.BillingName);
        Assert.Null(method.BillingCountry);
    }

    [Fact]
    public void A_removed_method_cannot_take_a_billing_address()
    {
        var method = NewMethod();
        method.Remove(DateTimeOffset.UtcNow);

        Assert.Throws<SavedPaymentMethodRuleException>(() =>
            method.SetBillingAddress(CardBillingAddress.Create("Ada", "1 St", "City", null, "3000", "AU"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void The_charge_snapshot_never_carries_the_billing_address()
    {
        var method = NewMethod();
        method.SetBillingAddress(CardBillingAddress.Create("Ada", "1 St", "City", null, "3000", "AU"), DateTimeOffset.UtcNow);

        // The money path is unchanged: what a charge reads from a saved card is still provider + token + display data.
        var snapshot = method.SnapshotForCharge();

        Assert.Equal(new SavedPaymentMethodSnapshot("stripe", "pm_card_visa", "visa", "4242"), snapshot);
    }

    private static SavedPaymentMethod NewMethod() => new()
    {
        Id = Guid.CreateVersion7(),
        PaymentCustomerId = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        UserId = Guid.CreateVersion7(),
        Provider = "stripe",
        ProviderPaymentMethodId = "pm_card_visa",
        Brand = "visa",
        Last4 = "4242",
        ExpMonth = 12,
        ExpYear = 2030,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
