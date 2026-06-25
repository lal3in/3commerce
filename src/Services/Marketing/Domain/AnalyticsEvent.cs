using System.Net;

namespace ThreeCommerce.Marketing.Domain;

/// <summary>A raw analytics event as submitted by the storefront collector (mt5_4).</summary>
public sealed record AnalyticsEventInput(
    int SchemaVersion,
    string EventType,
    string? VisitorId,
    string? SessionId,
    Guid? CustomerId,
    bool AnalyticsConsent,
    DateTimeOffset OccurredAt,
    string EventId,
    IReadOnlyDictionary<string, string>? Payload);

/// <summary>An accepted, sanitized analytics event ready to append (mt5_4). Stored append-only as JSONB.</summary>
public sealed record AnalyticsEvent(
    Guid TenantId,
    int SchemaVersion,
    string EventType,
    string? VisitorId,
    string? SessionId,
    Guid? CustomerId,
    bool AnalyticsConsent,
    DateTimeOffset OccurredAt,
    string EventId,
    IReadOnlyDictionary<string, string> Payload);

public sealed record AnalyticsBatchResult(IReadOnlyList<AnalyticsEvent> Accepted, IReadOnlyList<string> Rejected);

/// <summary>
/// Coarsens a client IP (mt5_4 GOTCHA): never store a raw IP. IPv4 is truncated to /24 and IPv6 to /48 —
/// enough for coarse geo, not enough to identify a person.
/// </summary>
public static class IpAnonymizer
{
    public static string Anonymize(string? rawIp)
    {
        if (string.IsNullOrWhiteSpace(rawIp) || !IPAddress.TryParse(rawIp, out var ip))
        {
            return "unknown";
        }

        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            bytes[3] = 0; // /24
        }
        else
        {
            for (var i = 6; i < bytes.Length; i++)
            {
                bytes[i] = 0; // /48
            }
        }

        return new IPAddress(bytes).ToString();
    }
}

/// <summary>
/// Strips payment/account form data from an event payload (mt5_4 GOTCHA). Key matching is
/// punctuation/case-insensitive so <c>card_number</c>, <c>CardNumber</c>, and <c>cardNumber</c> all drop.
/// </summary>
public static class AnalyticsPayload
{
    private static readonly HashSet<string> Blocked = new(StringComparer.Ordinal)
    {
        "card", "cardnumber", "pan", "cvv", "cvc", "securitycode", "expiry", "expirydate",
        "password", "passwd", "pwd", "pin", "ssn", "accountnumber", "account", "iban", "bsb", "routingnumber",
    };

    public static IReadOnlyDictionary<string, string> Sanitize(IReadOnlyDictionary<string, string>? payload)
    {
        if (payload is null || payload.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        return payload.Where(kv => !Blocked.Contains(Normalize(kv.Key))).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static string Normalize(string key) => new(key.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>
/// Accepts a batch of analytics events (mt5_4): validates the schema version + required fields, dedupes by
/// event id (idempotent — a re-sent id is silently skipped, not an error), and sanitizes each payload.
/// Invalid events are reported, never thrown, so a bad event in a batch doesn't sink the rest.
/// </summary>
public static class AnalyticsCollector
{
    public const int CurrentSchemaVersion = 1;

    public static AnalyticsBatchResult Accept(Guid tenantId, IEnumerable<AnalyticsEventInput> inputs, ISet<string> alreadyStored)
    {
        var accepted = new List<AnalyticsEvent>();
        var rejected = new List<string>();
        var seenInBatch = new HashSet<string>(StringComparer.Ordinal);

        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.EventId) || string.IsNullOrWhiteSpace(input.EventType)
                || input.SchemaVersion < 1 || input.SchemaVersion > CurrentSchemaVersion)
            {
                rejected.Add(input.EventId ?? string.Empty);
                continue;
            }

            if (alreadyStored.Contains(input.EventId) || !seenInBatch.Add(input.EventId))
            {
                continue; // idempotent: a duplicate id is a no-op
            }

            accepted.Add(new AnalyticsEvent(
                tenantId, input.SchemaVersion, input.EventType, input.VisitorId, input.SessionId, input.CustomerId,
                input.AnalyticsConsent, input.OccurredAt, input.EventId, AnalyticsPayload.Sanitize(input.Payload)));
        }

        return new AnalyticsBatchResult(accepted, rejected);
    }
}
