using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ThreeCommerce.Identity.Api.Endpoints;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// End-to-end guard for the session introspection cache with the feature flag turned ON (ADR-0044) —
/// driving the real Identity AuthService over HTTP against a real Redis. Complements the component-level
/// RedisSessionCacheTests (which cover the cache mechanics + eviction, incl. whole-user eviction) and the
/// cache-OFF IdentityAuthTests. This proves the wired path: cache-aside serves a valid session AND — the
/// security-critical part — logout eviction still makes the very next introspection fail with the cache
/// enabled (a stale cache hit here would be a logged-out session surviving).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class SessionCacheAuthFlowTests(Phase2Fixture fixture, RedisFixture redis) : IClassFixture<RedisFixture>
{
    private WebApplicationFactory<ThreeCommerce.Identity.Api.IApiMarker> CacheOnIdentity() =>
        fixture.CreateIdentityFactory(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = redis.ConnectionString,
            ["Sessions:Cache:Enabled"] = "true",
        });

    private sealed record IntrospectResponse(Guid SessionId, Guid UserId, string Role, DateTimeOffset ExpiresAt);

    private static string ExtractSessionCookie(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(AuthEndpoints.SessionCookieName, StringComparison.Ordinal));
        return setCookie.Split(';')[0].Split('=', 2)[1];
    }

    // Peek at Redis directly (via a fresh connection to the same container) to confirm the cache is
    // actually active — the per-user set proves a session was cached without needing the raw token hash.
    private async Task<bool> UserCachedAsync(Guid userId)
    {
        var connection = redis.NewConnection();
        _ = connection.Multiplexer;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (!connection.IsAvailable && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        return await connection.Database!.KeyExistsAsync($"sess:usr:{userId:N}");
    }

    [Fact]
    public async Task With_cache_on_the_session_is_cached_then_logout_evicts_it_and_the_next_introspection_fails()
    {
        using var identity = CacheOnIdentity();
        using var client = identity.CreateClient(new() { HandleCookies = false });
        var email = $"sesscache-{Guid.NewGuid():N}@example.com";

        await client.PostAsJsonAsync("/register", new { email, password = "a-strong-password" });
        var login = await client.PostAsJsonAsync("/login", new { email, password = "a-strong-password" });
        login.EnsureSuccessStatusCode();
        var sessionToken = ExtractSessionCookie(login);

        // First introspection: cache miss → Postgres → primes the cache.
        var first = await client.PostAsJsonAsync("/internal/introspection", new { token = sessionToken });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var session = (await first.Content.ReadFromJsonAsync<IntrospectResponse>())!;

        // The cache really is active (not silently disabled/falling back): the user's entry is in Redis.
        Assert.True(await UserCachedAsync(session.UserId), "session was not cached — the cache-on path is not actually active");

        // Second introspection: served from the Redis cache (cache-aside hit) — still a valid session.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/internal/introspection", new { token = sessionToken })).StatusCode);

        // Logout revokes in Postgres AND evicts the cached introspection.
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/logout");
        logout.Headers.Add("Cookie", $"{AuthEndpoints.SessionCookieName}={sessionToken}");
        (await client.SendAsync(logout)).EnsureSuccessStatusCode();

        // Eviction actually removed the entry from Redis...
        Assert.False(await UserCachedAsync(session.UserId), "logout did not evict the cached session from Redis");

        // ...so the very next introspection fails — no stale cache hit lets a logged-out session survive.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/internal/introspection", new { token = sessionToken })).StatusCode);
    }
}
