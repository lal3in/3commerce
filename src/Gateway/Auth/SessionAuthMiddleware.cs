using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace ThreeCommerce.Gateway.Auth;

/// <summary>
/// Gateway half of ADR-0012: opaque session cookie -> Identity introspection
/// (positive results cached ≤60s, bounding revocation lag / NFR-8) -> short-lived
/// signed claims header for services. Anonymous requests pass through untouched.
/// </summary>
public sealed partial class SessionAuthMiddleware(
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
            var session = await ResolveSessionAsync(rawToken, context.Request.Path, context.RequestAborted);
            if (session is not null)
            {
                context.Request.Headers[ClaimsHeader] = minter.Mint(
                    session.UserId, session.Role, session.SessionId, session.TenantId, session.Email,
                    session.Amr, session.StrongAuthAt, session.EmailVerified, session.SupplierEntityId);
            }
        }

        await next(context);
    }

    private async Task<IntrospectedSession?> ResolveSessionAsync(string rawToken, PathString path, CancellationToken ct)
    {
        var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        var cacheKey = "sess:" + tokenHash;
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
                Rejected(tokenHash, path, "introspection " + (int)response.StatusCode);
                return null;
            }

            var session = await response.Content.ReadFromJsonAsync<IntrospectedSession>(ct);
            if (session is null)
            {
                Rejected(tokenHash, path, "empty introspection");
                return null;
            }

            var ttl = session.ExpiresAt - DateTimeOffset.UtcNow;
            if (ttl <= TimeSpan.Zero)
            {
                Rejected(tokenHash, path, "expired");
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

    /// <summary>
    /// Security log (ADR-0065 Q2: this category is labelled log_category=security) for a session cookie the gateway
    /// refused — unknown, revoked, MFA-pending or expired. The request goes on anonymously, so a browser holding a
    /// stale cookie would log on every request: one line per token per minute. The token is never logged — only a
    /// 12-hex prefix of its SHA-256 (the introspection cache key), enough to correlate repeats.
    /// </summary>
    private void Rejected(string tokenHash, PathString path, string reason)
    {
        if (cache.TryGetValue("sess-rejected:" + tokenHash, out _))
            return;
        cache.Set("sess-rejected:" + tokenHash, true, CacheTtl);
        LogSessionRejected(logger, reason, tokenHash[..12], path.Value ?? "/");
    }

    [LoggerMessage(EventId = 4301, EventName = "SessionRejected", Level = LogLevel.Warning,
        Message = "Security: session cookie rejected ({Reason}; session ref {SessionRef}) on {Path} — treated as anonymous")]
    private static partial void LogSessionRejected(ILogger logger, string reason, string sessionRef, string path);

    private sealed record IntrospectedSession(
        Guid SessionId, Guid UserId, Guid TenantId, string Role, string Email, DateTimeOffset ExpiresAt,
        DateTimeOffset? StrongAuthAt = null, string? Amr = null, bool EmailVerified = false, Guid? SupplierEntityId = null);
}
