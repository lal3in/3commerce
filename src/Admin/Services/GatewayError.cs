using System.Text.Json;

namespace ThreeCommerce.Admin.Services;

/// <summary>
/// Turns a failed gateway response body into human-readable text for the admin UI.
/// RFC 7807 problem+json bodies are unwrapped to their <c>detail</c> (or <c>title</c>)
/// field so operators see a friendly message instead of the raw JSON envelope; any other
/// body is returned as-is.
/// </summary>
public static class GatewayError
{
    public static async Task<string> ReadAsync(HttpResponseMessage response)
        => Humanize(await response.Content.ReadAsStringAsync());

    public static string Humanize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !raw.TrimStart().StartsWith('{'))
        {
            return raw ?? string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (TryReadString(doc.RootElement, "detail", out var detail)) return detail;
                if (TryReadString(doc.RootElement, "title", out var title)) return title;
            }
        }
        catch (JsonException)
        {
            // Not JSON after all — fall through to the raw body.
        }

        return raw;
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
