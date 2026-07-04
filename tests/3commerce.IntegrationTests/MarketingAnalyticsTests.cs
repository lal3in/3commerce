using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Analytics event collection (def_4 / mt5_5): the anonymous batch endpoint dedupes by client
/// event id (retry-safe), sanitizes payment-shaped payload keys (mt5_4), stores only a coarse IP,
/// and caps the batch size. Consent is enforced at the storefront batcher — the server never sees
/// unconsented traffic.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class MarketingAnalyticsTests(Phase4Fixture fixture)
{
    private sealed record CollectResult(int Accepted, List<string> Rejected);
    private sealed record EventRow(Guid Id, string EventType, string? VisitorId, string? SessionId,
        DateTimeOffset OccurredAt, string EventId, string PayloadJson, string ClientIpCoarse, DateTimeOffset ReceivedAt);

    private static object Event(string id, string type = "page_view", Dictionary<string, string>? payload = null) => new
    {
        schemaVersion = 1,
        eventType = type,
        visitorId = "v-1",
        sessionId = "s-1",
        analyticsConsent = true,
        occurredAt = DateTimeOffset.UtcNow,
        eventId = id,
        payload,
    };

    [Fact]
    public async Task Batches_are_deduped_sanitized_and_capped()
    {
        using var client = fixture.Marketing.CreateClient();
        var tenant = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("X-3C-Tenant-Id", tenant.ToString());
        var id1 = $"e-{Guid.NewGuid():N}";
        var id2 = $"e-{Guid.NewGuid():N}";

        // First batch: 2 accepted; a payment-shaped payload key is dropped, the rest kept.
        var first = await client.PostAsJsonAsync("/events", new
        {
            events = new[]
            {
                Event(id1, "page_view", new() { ["path"] = "/au", ["cardNumber"] = "4242424242424242" }),
                Event(id2, "add_to_cart"),
            },
        });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(2, (await first.Content.ReadFromJsonAsync<CollectResult>())!.Accepted);

        // Retrying the same batch is a no-op (idempotent by event id).
        var retry = await client.PostAsJsonAsync("/events", new { events = new[] { Event(id1), Event(id2) } });
        Assert.Equal(0, (await retry.Content.ReadFromJsonAsync<CollectResult>())!.Accepted);

        // Admin read-back: sanitized payload, coarse IP only.
        using var admin = fixture.Marketing.CreateClient();
        admin.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));
        var rows = (await admin.GetFromJsonAsync<List<EventRow>>($"/admin/analytics/events?tenantId={tenant}"))!;
        Assert.Equal(2, rows.Count);
        var pageView = Assert.Single(rows, r => r.EventId == id1);
        Assert.Contains("path", pageView.PayloadJson);
        Assert.DoesNotContain("4242424242424242", pageView.PayloadJson);
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.ClientIpCoarse)));

        // Over-cap batch is rejected outright, not truncated.
        var big = await client.PostAsJsonAsync("/events", new
        {
            events = Enumerable.Range(0, 51).Select(i => Event($"big-{i}")).ToArray(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, big.StatusCode);
    }

    [Fact]
    public async Task Admin_listing_requires_the_admin_role()
    {
        using var client = fixture.Marketing.CreateClient();
        var anon = await client.GetAsync($"/admin/analytics/events?tenantId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
    }
}
