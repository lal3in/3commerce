using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace ThreeCommerce.Gateway.Auth;

/// <summary>
/// Gateway half of ADR-0012: opaque session cookie -> Identity introspection
/// (positive results cached ≤60s, bounding revocation lag / NFR-8) -> short-lived
/// signed claims header for services. Anonymous requests pass through untouched.
/// </summary>
public sealed class SessionAuthMiddleware(
    RequestDelegate next,
    IMemoryCache cache,
    IHttpClientFactory httpClientFactory,
    InternalClaimsMinter minter,
    ILogger<SessionAuthMiddleware> logger)
{
    public const string CookieName = "3c_session";
    public const string ClaimsHeader = "X-Internal-Claims";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public async Task InvokeAsync(HttpContext context)
    {
        // Inbound header is always attacker-controlled — strip before anything else.
        context.Request.Headers.Remove(ClaimsHeader);

        if (context.Request.Cookies.TryGetValue(CookieName, out var rawToken) && rawToken.Length > 0)
        {
            var session = await ResolveSessionAsync(rawToken, context.RequestAborted);
            if (session is not null)
            {
                context.Request.Headers[ClaimsHeader] = minter.Mint(session.UserId, session.Role, session.SessionId);
            }
        }

        await next(context);
    }

    private async Task<IntrospectedSession?> ResolveSessionAsync(string rawToken, CancellationToken ct)
    {
        var cacheKey = "sess:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        if (cache.TryGetValue<IntrospectedSession>(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            var client = httpClientFactory.CreateClient("identity");
            using var response = await client.PostAsJsonAsync("/internal/introspection", new { token = rawToken }, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var session = await response.Content.ReadFromJsonAsync<IntrospectedSession>(ct);
            if (session is null)
            {
                return null;
            }

            var ttl = session.ExpiresAt - DateTimeOffset.UtcNow;
            if (ttl <= TimeSpan.Zero)
            {
                return null;
            }

            cache.Set(cacheKey, session, ttl < CacheTtl ? ttl : CacheTtl);
            return session;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Identity down: fail closed for authenticated surface, but don't 500 the request.
            logger.LogWarning(ex, "Session introspection failed; treating request as anonymous");
            return null;
        }
    }

    private sealed record IntrospectedSession(Guid SessionId, Guid UserId, string Role, DateTimeOffset ExpiresAt);
}
