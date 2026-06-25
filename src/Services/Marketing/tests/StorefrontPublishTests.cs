using ThreeCommerce.Marketing.Domain;

namespace ThreeCommerce.Marketing.Tests;

public class StorefrontPublishTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    private static PublishableContent Content() => PublishableContent.Create(Tenant, "home", "{\"v\":1}", Now);

    [Fact]
    public void Create_starts_as_draft_v1_with_nothing_published()
    {
        var content = Content();
        Assert.Equal(PublishStatus.Draft, content.Status);
        Assert.Equal(1, content.DraftVersion);
        Assert.Null(content.PublishedVersion);
        Assert.Null(content.PublishedPayload);
    }

    [Fact]
    public void Saving_a_draft_versions_the_content()
    {
        var content = Content();
        content.SaveDraft("{\"v\":2}", Now);
        Assert.Equal(2, content.DraftVersion);
        Assert.Equal("{\"v\":2}", content.DraftPayload);
        Assert.Equal(2, content.Versions.Count);
    }

    [Fact]
    public void Publishing_snapshots_the_current_draft()
    {
        var content = Content();
        content.SaveDraft("{\"v\":2}", Now);
        content.Publish(Now);

        Assert.Equal(PublishStatus.Published, content.Status);
        Assert.Equal(2, content.PublishedVersion);
        Assert.Equal("{\"v\":2}", content.PublishedPayload);
    }

    [Fact]
    public void A_scheduled_publish_goes_live_only_once_its_time_arrives()
    {
        var content = Content();
        content.SchedulePublish(Now.AddHours(2), Now);
        Assert.Equal(PublishStatus.Scheduled, content.Status);

        Assert.False(content.PublishDueScheduled(Now.AddHours(1))); // not yet
        Assert.Null(content.PublishedVersion);

        Assert.True(content.PublishDueScheduled(Now.AddHours(3)));  // due
        Assert.Equal(PublishStatus.Published, content.Status);
        Assert.Equal(1, content.PublishedVersion);
    }

    [Fact]
    public void Scheduling_in_the_past_is_rejected()
    {
        Assert.Throws<MarketingRuleException>(() => Content().SchedulePublish(Now.AddHours(-1), Now));
    }

    [Fact]
    public void Rollback_restores_a_prior_version_as_published()
    {
        var content = Content();      // v1
        content.Publish(Now);          // published v1
        content.SaveDraft("{\"v\":2}", Now);
        content.Publish(Now);          // published v2

        content.Rollback(1, Now);
        Assert.Equal(1, content.PublishedVersion);
        Assert.Equal("{\"v\":1}", content.PublishedPayload);

        Assert.Throws<MarketingRuleException>(() => content.Rollback(99, Now));
    }

    [Fact]
    public void A_preview_token_targets_the_draft_and_expires()
    {
        var content = Content();
        content.SaveDraft("{\"v\":2}", Now);

        var token = content.Preview(Now, TimeSpan.FromMinutes(30));
        Assert.Equal(content.Id, token.ContentId);
        Assert.Equal(2, token.Version);            // read-only pointer at the draft
        Assert.True(token.IsValid(Now.AddMinutes(10)));
        Assert.False(token.IsValid(Now.AddMinutes(31)));
    }
}
