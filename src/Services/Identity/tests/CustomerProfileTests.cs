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
    public void Customer_profile_names_are_structured_with_display_and_full_name()
    {
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = "shopper@example.test",
            PasswordHash = "hash",
            Title = "Ms",
            FirstName = "Ada",
            MiddleName = "King",
            LastName = "Lovelace",
            PreferredName = "Ada L.",
            Phone = "+61400000000",
            DateOfBirth = new DateOnly(1990, 5, 1),
            MarketingConsent = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal("Ada", user.FirstName);
        Assert.Equal("Lovelace", user.LastName);
        Assert.Equal("Ada L.", user.DisplayName);            // preferred name wins
        Assert.Equal("Ms Ada King Lovelace", user.FullName); // full legal name for billing
    }

    [Fact]
    public void DisplayName_falls_back_to_first_last_then_email_local_part()
    {
        var named = new User { Id = Guid.CreateVersion7(), Email = "x@e.test", PasswordHash = "h", FirstName = "Grace", LastName = "Hopper" };
        Assert.Equal("Grace Hopper", named.DisplayName);

        var anon = new User { Id = Guid.CreateVersion7(), Email = "solo@e.test", PasswordHash = "h" };
        Assert.Equal("solo", anon.DisplayName);
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
