using System.Security.Claims;

namespace ThreeCommerce.Admin.Services;

/// <summary>
/// Streams a support-ticket attachment to the operator's browser: the admin app can't be linked
/// directly to the gateway (the browser has no gateway auth), so this same-origin endpoint forwards
/// the operator's session cookie and streams the file back. Mirrors the storefront's proxy route.
/// </summary>
public static class SupportAttachmentEndpoints
{
    public static void MapSupportAttachmentProxy(this WebApplication app)
    {
        app.MapGet("/support-attachment/{id:guid}", async (Guid id, HttpContext http, IHttpClientFactory factory) =>
        {
            var token = http.User.FindFirstValue(GatewayClient.SessionClaim);
            if (string.IsNullOrEmpty(token))
            {
                return Results.Unauthorized();
            }

            var client = factory.CreateClient("gateway");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/support/attachments/{id}");
            request.Headers.Add("Cookie", $"3c_session={token}");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return Results.StatusCode((int)response.StatusCode);
            }

            var stream = await response.Content.ReadAsStreamAsync();
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName;
            return Results.Stream(stream, contentType, fileName?.Trim('"'));
        }).RequireAuthorization();
    }
}
