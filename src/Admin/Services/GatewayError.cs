using System.Text.Json;

namespace ThreeCommerce.Admin.Services;

/// <summary>
/// Turns a failed gateway response body into human-readable text for the admin UI.
/// RFC 7807 problem+json bodies are unwrapped so operators see a friendly message instead
/// of the raw JSON envelope. Precedence: the <c>errors</c> dictionary of a ValidationProblem
/// (flattened to <c>field: message</c>) beats <c>detail</c> beats <c>title</c> — because a
/// ValidationProblem's title is the useless "One or more validation errors occurred." and the
/// real information lives in <c>errors</c>. A bare JSON string body (e.g. a
/// <c>TypedResults.Conflict&lt;string&gt;</c>) is unwrapped to its text. Any other body is
/// returned as-is.
/// </summary>
public static class GatewayError
{
    public static async Task<string> ReadAsync(HttpResponseMessage response)
        => Humanize(await response.Content.ReadAsStringAsync());

    public static string Humanize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw ?? string.Empty;
        }

        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('"'))
        {
            return raw;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var value = root.GetString();
                return string.IsNullOrWhiteSpace(value) ? raw : value!;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryReadErrors(root, out var errors)) return errors;
                if (TryReadString(root, "detail", out var detail)) return detail;
                if (TryReadString(root, "title", out var title)) return title;
            }
        }
        catch (JsonException)
        {
            // Not JSON after all — fall through to the raw body.
        }

        return raw;
    }

    /// <summary>
    /// Flattens an ASP.NET ValidationProblem <c>errors</c> object ({field: [messages]}) into a
    /// compact <c>field: message; field2: message</c> string. Model-level errors (empty or "$"
    /// key) contribute their message without a field prefix.
    /// </summary>
    private static bool TryReadErrors(JsonElement root, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var parts = new List<string>();
        foreach (var field in errors.EnumerateObject())
        {
            var message = field.Value.ValueKind switch
            {
                JsonValueKind.Array => string.Join(" ", field.Value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString())),
                JsonValueKind.String => field.Value.GetString(),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            parts.Add(string.IsNullOrEmpty(field.Name) || field.Name == "$" ? message! : $"{field.Name}: {message}");
        }

        if (parts.Count == 0)
        {
            return false;
        }

        value = string.Join("; ", parts);
        return true;
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
