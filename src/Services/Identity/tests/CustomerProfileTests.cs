using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.Identity.Tests;

public class CustomerProfileTests
{
    [Fact]
    public void Address_can_be_used_for_its_purpose_or_both()
    {
        var billing = Address(AddressPurpose.Billing);
        var shipping = Address(AddressPurpose.Shipping);
        var both = Address(AddressPurpose.Both);

        Assert.True(billing.CanBeUsedFor(AddressPurpose.Billing));
        Assert.False(billing.CanBeUsedFor(AddressPurpose.Shipping));
        Assert.True(shipping.CanBeUsedFor(AddressPurpose.Shipping));
        Assert.True(both.CanBeUsedFor(AddressPurpose.Billing));
        Assert.True(both.CanBeUsedFor(AddressPurpose.Shipping));
    }

    [Theory]
    [InlineData(AddressPurpose.Billing, AddressPurpose.Billing, true)]
    [InlineData(AddressPurpose.Shipping, AddressPurpose.Shipping, true)]
    [InlineData(AddressPurpose.Billing, AddressPurpose.Shipping, false)]
    [InlineData(AddressPurpose.Both, AddressPurpose.Billing, true)]
    [InlineData(AddressPurpose.Shipping, AddressPurpose.Both, true)]
    public void Default_address_conflicts_are_purpose_aware(AddressPurpose existing, AddressPurpose incoming, bool expected)
    {
        Assert.Equal(expected, AddressDefaultRules.DefaultsConflict(existing, incoming));
    }

    [Fact]
    public void Customer_profile_names_are_optional_on_user()
    {
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = "shopper@example.test",
            PasswordHash = "hash",
            GivenName = "Ada",
            FamilyName = "Lovelace",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal("Ada", user.GivenName);
        Assert.Equal("Lovelace", user.FamilyName);
    }

    private static Address Address(AddressPurpose purpose) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        Purpose = purpose,
        Name = "Shopper",
        Line1 = "1 Street",
        City = "Sydney",
        Postcode = "2000",
        Country = "AU",
    };
}
