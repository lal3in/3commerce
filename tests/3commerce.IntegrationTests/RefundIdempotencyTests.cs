using System.Net;
using System.Net.Http.Json;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// pay_7: the admin refund endpoint runs through the shared IIdempotencyGuard. A replayed
/// Idempotency-Key with the same body returns the stored response (no second refund); the same
/// key with a different body is a 409 problem+json carrying the machine-readable errorCode.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class RefundIdempotencyTests(Phase4Fixture fixture)
{
    private sealed record RefundResponseDto(Guid RefundId);

    private HttpClient Admin()
    {
        var client = fixture.Payments.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));
        return client;
    }

    private static HttpRequestMessage Refund(string key, Guid orderId, long amount, string reason)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/refunds")
        {
            Content = JsonContent.Create(new { orderId, amountMinor = amount, reason }),
        };
        req.Headers.Add("Idempotency-Key", key);
        return req;
    }

    [Fact]
    public async Task Missing_idempotency_key_is_rejected()
    {
        using var admin = Admin();
        var response = await admin.PostAsJsonAsync("/admin/refunds", new { orderId = Guid.NewGuid(), amountMinor = 500L, reason = "test" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Replayed_key_same_body_returns_the_stored_response()
    {
        using var admin = Admin();
        var key = $"idem-{Guid.NewGuid():N}";
        var orderId = Guid.NewGuid();

        var first = await admin.SendAsync(Refund(key, orderId, 1500, "duplicate charge"));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstBody = (await first.Content.ReadFromJsonAsync<RefundResponseDto>())!;

        var replay = await admin.SendAsync(Refund(key, orderId, 1500, "duplicate charge"));
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        var replayBody = (await replay.Content.ReadFromJsonAsync<RefundResponseDto>())!;

        // Same refund id — the operation did not run twice.
        Assert.Equal(firstBody.RefundId, replayBody.RefundId);
    }

    [Fact]
    public async Task Replayed_key_different_body_is_a_409_with_error_code()
    {
        using var admin = Admin();
        var key = $"idem-{Guid.NewGuid():N}";
        var orderId = Guid.NewGuid();

        var first = await admin.SendAsync(Refund(key, orderId, 1500, "duplicate charge"));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var conflict = await admin.SendAsync(Refund(key, orderId, 9999, "different amount"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var doc = System.Text.Json.JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("idempotency_conflict", doc.RootElement.GetProperty("errorCode").GetString());
    }
}
