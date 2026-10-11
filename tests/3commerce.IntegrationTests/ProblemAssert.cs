using System.Net;
using System.Text.Json;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Asserts the RFC 9457 shape every Ordering cart/checkout refusal must have (AGENTS.md "Error Handling"):
/// <c>application/problem+json</c>, the expected status, a non-empty title, the human message as
/// <c>detail</c>, and the numeric machine-readable code (<c>checkoutBlock</c> / <c>cartRefusal</c>).
/// Returns the parsed body so a test can check further extensions.
/// </summary>
internal static class ProblemAssert
{
    public static Task<JsonElement> CheckoutRefusedAsync(
        HttpResponseMessage response, HttpStatusCode status, int checkoutBlock, string? detail = null) =>
        RefusedAsync(response, status, "checkoutBlock", checkoutBlock, detail);

    public static Task<JsonElement> CartRefusedAsync(
        HttpResponseMessage response, HttpStatusCode status, int cartRefusal, string? detail = null) =>
        RefusedAsync(response, status, "cartRefusal", cartRefusal, detail);

    private static async Task<JsonElement> RefusedAsync(
        HttpResponseMessage response, HttpStatusCode status, string codeName, int code, string? detail)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"expected {(int)status}, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = JsonDocument.Parse(raw).RootElement.Clone();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()), "problem title is empty");
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()), "problem detail is empty");
        Assert.True(body.TryGetProperty("traceId", out _), "problem has no traceId");
        Assert.Equal(code, body.GetProperty(codeName).GetInt32());
        if (detail is not null)
        {
            Assert.Equal(detail, body.GetProperty("detail").GetString());
        }

        return body;
    }
}
