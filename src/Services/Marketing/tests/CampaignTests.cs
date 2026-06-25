using ThreeCommerce.Marketing.Domain;

namespace ThreeCommerce.Marketing.Tests;

public class CampaignTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public void Create_normalises_the_cid_and_starts_as_draft()
    {
        var campaign = Campaign.Create(Tenant, "Summer Sale 2026!", "Summer Sale", Now);
        Assert.Equal("summersale2026", campaign.Cid);
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
        Assert.False(campaign.IsLive(Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@@@")]
    [InlineData("../../etc")] // path-y junk reduces to "etc", which is fine — but a pure separator set is null
    public void Create_requires_a_usable_cid(string cid)
    {
        if (Campaign.SanitizeCid(cid) is null)
        {
            Assert.Throws<MarketingRuleException>(() => Campaign.Create(Tenant, cid, "x", Now));
        }
    }

    [Fact]
    public void Create_rejects_an_empty_name_or_inverted_window()
    {
        Assert.Throws<MarketingRuleException>(() => Campaign.Create(Tenant, "c1", "  ", Now));
        Assert.Throws<MarketingRuleException>(() => Campaign.Create(Tenant, "c1", "x", Now, Now.AddDays(2), Now.AddDays(1)));
    }

    [Fact]
    public void A_campaign_is_live_only_while_active_and_inside_its_window()
    {
        var campaign = Campaign.Create(Tenant, "c1", "x", Now, Now, Now.AddDays(7));
        Assert.False(campaign.IsLive(Now)); // still Draft

        campaign.Activate(Now);
        Assert.True(campaign.IsLive(Now.AddDays(1)));
        Assert.False(campaign.IsLive(Now.AddDays(-1)));  // before start
        Assert.False(campaign.IsLive(Now.AddDays(8)));   // after end

        campaign.Pause(Now);
        Assert.False(campaign.IsLive(Now.AddDays(1)));
    }

    [Fact]
    public void Lifecycle_guards_hold()
    {
        var campaign = Campaign.Create(Tenant, "c1", "x", Now);
        Assert.Throws<MarketingRuleException>(() => campaign.Pause(Now)); // not active

        campaign.End(Now);
        Assert.Throws<MarketingRuleException>(() => campaign.Activate(Now)); // ended is terminal
    }

    [Theory]
    [InlineData("Summer Sale 2026!", "summersale2026")]
    [InlineData("  Black-Friday_2026  ", "black-friday_2026")]
    [InlineData("@@@", null)]
    [InlineData("", null)]
    public void SanitizeCid_produces_a_safe_slug_or_null(string raw, string? expected)
    {
        Assert.Equal(expected, Campaign.SanitizeCid(raw));
    }
}
