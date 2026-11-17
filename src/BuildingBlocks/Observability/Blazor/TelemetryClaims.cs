using System.Net.Http.Json;
using System.Security.Claims;

namespace ThreeCommerce.BuildingBlocks.Observability.Blazor;

/// <summary>
/// Claim types the Blazor apps put in their auth cookie at sign-in so telemetry can name WHO without naming
/// them: opaque ids only. The display name / email claim is PII and is never read for telemetry.
/// </summary>
public static class TelemetryClaims
{
    public const string UserId = "3c_user_id";
    public const string TenantId = "3c_tenant_id";

    /// <summary>The gateway route of Identity's <c>GET /session</c> (the caller's opaque user + tenant ids).</summary>
    public const string SessionContextPath = "/api/identity/session";

    /// <summary>
    /// Best effort: asks Identity (through the gateway, with the just-issued session) for the principal's opaque
    /// ids and returns them as claims to add to the app's auth cookie. Any failure returns no claims — telemetry
    /// then reports the ids as <c>unknown</c>; sign-in never fails because of it.
    /// </summary>
    public static async Task<IReadOnlyList<Claim>> FetchAsync(
        HttpClient gateway, string sessionCookieHeader, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SessionContextPath);
            request.Headers.Add("Cookie", sessionCookieHeader);
            using var response = await gateway.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return [];

            var body = await response.Content.ReadFromJsonAsync<SessionContext>(cancellationToken);
            if (body is null || body.UserId == Guid.Empty)
                return [];

            var claims = new List<Claim> { new(UserId, body.UserId.ToString()) };
            if (body.TenantId is { } tenantId && tenantId != Guid.Empty)
                claims.Add(new Claim(TenantId, tenantId.ToString()));
            return claims;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return [];
        }
    }

    private sealed record SessionContext(Guid UserId, Guid? TenantId);
}
