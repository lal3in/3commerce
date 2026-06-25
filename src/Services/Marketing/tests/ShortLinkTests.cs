using ThreeCommerce.Marketing.Domain;

namespace ThreeCommerce.Marketing.Tests;

public class ShortLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string> { "shop.acme.com", "localhost" };

    [Fact]
    public void Generated_codes_are_valid_url_safe_and_vary()
    {
        var a = ShortCode.Generate();
        var b = ShortCode.Generate();
        Assert.True(ShortCode.IsValid(a));
        Assert.Equal(7, a.Length);
        Assert.NotEqual(a, b); // overwhelmingly likely
    }

    [Theory]
    [InlineData("abcd", true)]
    [InlineData("aB9_", false)]   // '_' not in base62
    [InlineData("abc", false)]    // too short
    [InlineData("0123456789abcdef0", false)] // too long
    public void Code_validation_enforces_length_and_alphabet(string code, bool valid)
    {
        Assert.Equal(valid, ShortCode.IsValid(code));
    }

    [Theory]
    [InlineData("https://shop.acme.com/p/1", true)]
    [InlineData("http://localhost:3000/sale", true)]
    [InlineData("https://evil.example.com/phish", false)] // unregistered host -> open redirect blocked
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/relative/path", false)]
    public void Destination_must_be_a_registered_storefront_host(string url, bool allowed)
    {
        Assert.Equal(allowed, ShortLinkDestination.IsAllowed(url, AllowedHosts, out _));
    }

    [Fact]
    public void Create_rejects_an_unregistered_destination()
    {
        Assert.Throws<MarketingRuleException>(
            () => ShortLink.Create(Tenant, "promo1", "https://evil.example.com", AllowedHosts, Now));
    }

    [Fact]
    public void Follow_records_a_click_then_returns_the_destination()
    {
        var link = ShortLink.Create(Tenant, "promo1", "https://shop.acme.com/sale", AllowedHosts, Now, cid: "Summer!");
        Assert.Equal("summer", link.Cid);

        Assert.Equal("https://shop.acme.com/sale", link.Follow(Now));
        Assert.Equal(1, link.ClickCount);

        link.Follow(Now);
        Assert.Equal(2, link.ClickCount); // each follow records a click
    }

    [Fact]
    public void A_disabled_or_expired_link_does_not_redirect()
    {
        var expiring = ShortLink.Create(Tenant, "promo2", "https://shop.acme.com/x", AllowedHosts, Now, expiresAt: Now.AddHours(1));
        Assert.Null(expiring.Follow(Now.AddHours(2)));     // expired
        Assert.Equal(0, expiring.ClickCount);              // no click on a non-redirect

        var link = ShortLink.Create(Tenant, "promo3", "https://shop.acme.com/y", AllowedHosts, Now);
        link.Disable();
        Assert.Null(link.Follow(Now));
    }
}
