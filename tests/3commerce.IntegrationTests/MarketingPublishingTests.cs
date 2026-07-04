using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Marketing.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Publishing persistence (def_5 / mt5_7): the tested PublishableContent aggregate round-trips
/// through EF (versions append-only), the lifecycle endpoints drive it, due schedules publish via
/// the sweep, and preview links are signed + expiring while published content is key-addressable.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class MarketingPublishingTests(Phase4Fixture fixture)
{
    private sealed record Detail(Guid Id, string Key, int Status, int DraftVersion, int? PublishedVersion,
        DateTimeOffset? ScheduledAt, string DraftPayload, string? PublishedPayload, List<int> Versions, DateTimeOffset UpdatedAt);
    private sealed record Token(Guid ContentId, int Version, DateTimeOffset ExpiresAt, string PreviewPath);
    private sealed record Published(string Key, int Version, string Payload);

    private HttpClient Admin()
    {
        var client = fixture.Marketing.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));
        return client;
    }

    [Fact]
    public async Task Draft_publish_rollback_lifecycle_persists_versions_append_only()
    {
        using var admin = Admin();
        var tenant = Guid.NewGuid();
        var key = $"home-hero-{Guid.NewGuid():N}";

        var create = await admin.PostAsJsonAsync($"/admin/content/?tenantId={tenant}", new { key, payload = "v1 copy" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var content = (await create.Content.ReadFromJsonAsync<Detail>())!;

        // Duplicate key rejected.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/admin/content/?tenantId={tenant}", new { key, payload = "x" })).StatusCode);

        // New draft version; history retained.
        var draft = await admin.PutAsJsonAsync($"/admin/content/{content.Id}/draft?tenantId={tenant}", new { payload = "v2 copy" });
        var afterDraft = (await draft.Content.ReadFromJsonAsync<Detail>())!;
        Assert.Equal(2, afterDraft.DraftVersion);
        Assert.Equal([1, 2], afterDraft.Versions);

        // Publish v2, roll back to v1 — published payload follows.
        await admin.PostAsync($"/admin/content/{content.Id}/publish?tenantId={tenant}", null);
        var rollback = await admin.PostAsJsonAsync($"/admin/content/{content.Id}/rollback?tenantId={tenant}", new { version = 1 });
        var afterRollback = (await rollback.Content.ReadFromJsonAsync<Detail>())!;
        Assert.Equal(1, afterRollback.PublishedVersion);
        Assert.Equal("v1 copy", afterRollback.PublishedPayload);

        // Published content is key-addressable anonymously (storefront rendering).
        using var anon = fixture.Marketing.CreateClient();
        var published = (await anon.GetFromJsonAsync<Published>($"/content/{key}?tenantId={tenant}"))!;
        Assert.Equal("v1 copy", published.Payload);
    }

    [Fact]
    public async Task Due_schedules_publish_via_the_sweep()
    {
        using var admin = Admin();
        var tenant = Guid.NewGuid();
        var key = $"sale-banner-{Guid.NewGuid():N}";
        var create = await admin.PostAsJsonAsync($"/admin/content/?tenantId={tenant}", new { key, payload = "sale!" });
        var content = (await create.Content.ReadFromJsonAsync<Detail>())!;

        // Past schedule rejected; near-future schedule accepted.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/admin/content/{content.Id}/schedule?tenantId={tenant}",
                new { at = DateTimeOffset.UtcNow.AddMinutes(-1) })).StatusCode);
        var schedule = await admin.PostAsJsonAsync($"/admin/content/{content.Id}/schedule?tenantId={tenant}",
            new { at = DateTimeOffset.UtcNow.AddMilliseconds(300) });
        Assert.Equal(2, (await schedule.Content.ReadFromJsonAsync<Detail>())!.Status); // Scheduled

        // Run the sweep the Quartz job fires (the job itself is cron-driven; the sweep is the logic).
        await Task.Delay(400);
        using var scope = fixture.Marketing.Services.CreateScope();
        var published = await scope.ServiceProvider.GetRequiredService<PublishingService>().SweepDueScheduledAsync(CancellationToken.None);
        Assert.True(published >= 1);

        var detail = (await admin.GetFromJsonAsync<Detail>($"/admin/content/{content.Id}?tenantId={tenant}"))!;
        Assert.Equal(3, detail.Status); // Published
        Assert.Equal(1, detail.PublishedVersion);
    }

    [Fact]
    public async Task Preview_links_are_signed_and_reject_tampering()
    {
        using var admin = Admin();
        var tenant = Guid.NewGuid();
        var create = await admin.PostAsJsonAsync($"/admin/content/?tenantId={tenant}",
            new { key = $"pv-{Guid.NewGuid():N}", payload = "draft body" });
        var content = (await create.Content.ReadFromJsonAsync<Detail>())!;

        var mint = await admin.PostAsync($"/admin/content/{content.Id}/preview-token?tenantId={tenant}", null);
        var token = (await mint.Content.ReadFromJsonAsync<Token>())!;

        using var anon = fixture.Marketing.CreateClient();
        var ok = await anon.GetAsync(token.PreviewPath);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("draft body", (await ok.Content.ReadFromJsonAsync<Published>())!.Payload);

        // Tampering with the target version invalidates the signature.
        var tampered = token.PreviewPath.Replace($"/{token.Version}?", $"/{token.Version + 1}?");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(tampered)).StatusCode);
    }
}
