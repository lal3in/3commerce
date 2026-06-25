using ThreeCommerce.Marketing.Domain;

namespace ThreeCommerce.Marketing.Tests;

public class AttributionTests
{
    private static readonly DateTimeOffset Convert = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Google", "google")]
    [InlineData("  Email_Newsletter  ", "email_newsletter")]
    [InlineData("<script>", "script")]
    [InlineData("@@@", null)]
    [InlineData("", null)]
    public void Utm_sanitize_produces_a_safe_token_or_null(string raw, string? expected)
    {
        Assert.Equal(expected, Utm.Sanitize(raw));
    }

    [Fact]
    public void Gclid_keeps_its_case_unlike_source_and_medium()
    {
        var touch = Utm.Touch("Promo-1", "Google", "CPC", "Cj0KCQiAbC123", Convert);
        Assert.Equal("promo-1", touch.Cid);
        Assert.Equal("google", touch.Source);
        Assert.Equal("cpc", touch.Medium);
        Assert.Equal("Cj0KCQiAbC123", touch.Gclid); // gclid is opaque — not lower-cased
    }

    [Fact]
    public void A_touch_with_only_junk_has_no_attribution()
    {
        var touch = Utm.Touch("@@@", "  ", null, "###", Convert);
        Assert.False(touch.HasAttribution);
    }

    [Fact]
    public void Last_click_credits_the_most_recent_touch_in_the_window()
    {
        var touches = new List<AttributionTouch>
        {
            Utm.Touch("spring", "google", "cpc", null, Convert.AddDays(-20)),
            Utm.Touch("summer", "email", "newsletter", null, Convert.AddDays(-2)),
            Utm.Touch("flash", "facebook", "social", null, Convert.AddDays(-1)),
        };

        var credited = Attribution.Attribute(touches, Convert);
        Assert.Equal("flash", credited!.Cid);

        var firstClick = Attribution.Attribute(touches, Convert, AttributionModel.FirstClick);
        Assert.Equal("spring", firstClick!.Cid);
    }

    [Fact]
    public void Touches_outside_the_window_and_non_attribution_touches_are_ignored()
    {
        var touches = new List<AttributionTouch>
        {
            Utm.Touch("old", "google", "cpc", null, Convert.AddDays(-40)),  // outside 30-day window
            Utm.Touch(null, null, null, null, Convert.AddDays(-1)),         // no attribution
        };

        Assert.Null(Attribution.Attribute(touches, Convert));
        Assert.Equal("old", Attribution.Attribute(touches, Convert, window: TimeSpan.FromDays(60))!.Cid);
    }

    [Fact]
    public void Landing_path_targets_are_normalised()
    {
        Assert.Equal("/sale/summer", CampaignTarget.LandingPath("Sale/Summer").Value);
        Assert.Equal("/sale", CampaignTarget.LandingPath("/Sale").Value);
        Assert.Equal(CampaignTargetType.LandingPath, CampaignTarget.LandingPath("x").Type);
    }
}
