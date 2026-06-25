namespace ThreeCommerce.Marketing.Domain;

public enum AttributionModel { LastClick = 1, FirstClick = 2 }

public enum CampaignTargetType { Storefront = 1, Product = 2, LandingPath = 3 }

/// <summary>Where a campaign points (mt5_2): a storefront, a product, or a landing path.</summary>
public sealed record CampaignTarget(CampaignTargetType Type, string Value)
{
    public static CampaignTarget LandingPath(string path)
    {
        var trimmed = (path ?? string.Empty).Trim();
        var normalized = trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
        return new CampaignTarget(CampaignTargetType.LandingPath, normalized.ToLowerInvariant());
    }
}

/// <summary>
/// One recorded marketing touch (mt5_2): the (sanitized) campaign + source/medium/gclid that led to a
/// visit. All touches are stored; attribution chooses among them. Built only from sanitized inputs.
/// </summary>
public sealed record AttributionTouch(string? Cid, string? Source, string? Medium, string? Gclid, DateTimeOffset OccurredAt)
{
    public bool HasAttribution => Cid is not null || Source is not null || Gclid is not null;
}

/// <summary>
/// Sanitizes external attribution parameters (mt5_2). utm_*/gclid come from untrusted query strings, so
/// every value is reduced to a safe token and an unusable one becomes null — never an exception, so a
/// malformed link can't break page load (mt5_2 GOTCHA). Campaign ids reuse <see cref="Campaign.SanitizeCid"/>.
/// </summary>
public static class Utm
{
    public static string? Sanitize(string? raw, bool lowerCase = true, int maxLength = 64)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (lowerCase)
        {
            value = value.ToLowerInvariant();
        }

        var cleaned = new string(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray()).Trim('-', '_', '.');
        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }

    public static AttributionTouch Touch(string? cid, string? source, string? medium, string? gclid, DateTimeOffset now) =>
        new(Campaign.SanitizeCid(cid), Sanitize(source), Sanitize(medium), Sanitize(gclid, lowerCase: false, maxLength: 128), now);
}

/// <summary>Resolves which touch gets credit for a conversion (mt5_2): last-click v1, within a window.</summary>
public static class Attribution
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    public static AttributionTouch? Attribute(
        IReadOnlyList<AttributionTouch> touches, DateTimeOffset conversionAt, AttributionModel model = AttributionModel.LastClick, TimeSpan? window = null)
    {
        var earliest = conversionAt - (window ?? DefaultWindow);
        var eligible = touches
            .Where(t => t.HasAttribution && t.OccurredAt >= earliest && t.OccurredAt <= conversionAt)
            .OrderBy(t => t.OccurredAt)
            .ToList();

        if (eligible.Count == 0)
        {
            return null;
        }

        return model == AttributionModel.FirstClick ? eligible[0] : eligible[^1];
    }
}
