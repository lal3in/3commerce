namespace ThreeCommerce.Marketing.Domain;

public sealed class MarketingRuleException(string message) : Exception(message);

public enum CampaignStatus { Draft = 1, Active = 2, Paused = 3, Ended = 4 }

/// <summary>
/// A marketing campaign (mt5_1). Identified by a URL-safe <see cref="Cid"/> that appears in inbound links;
/// it is live only while Active and inside its optional run window. The Marketing service owns campaigns,
/// targets, links, events, and conversions (high-volume analytics ingest batched + projected async).
/// </summary>
public sealed class Campaign
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string Cid { get; init; }
    public required string Name { get; init; }
    public CampaignStatus Status { get; private set; } = CampaignStatus.Draft;
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? EndsAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Campaign() { }

    public static Campaign Create(
        Guid tenantId, string cid, string name, DateTimeOffset now, DateTimeOffset? startsAt = null, DateTimeOffset? endsAt = null)
    {
        var normalized = SanitizeCid(cid) ?? throw new MarketingRuleException("A campaign id must contain letters, digits, '-' or '_'.");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new MarketingRuleException("A campaign needs a name.");
        }

        if (startsAt is { } start && endsAt is { } end && end <= start)
        {
            throw new MarketingRuleException("A campaign's end must be after its start.");
        }

        return new Campaign
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Cid = normalized,
            Name = name.Trim(),
            StartsAt = startsAt,
            EndsAt = endsAt,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Activate(DateTimeOffset now)
    {
        if (Status == CampaignStatus.Ended)
        {
            throw new MarketingRuleException("An ended campaign cannot be reactivated.");
        }

        Status = CampaignStatus.Active;
        UpdatedAt = now;
    }

    public void Pause(DateTimeOffset now)
    {
        if (Status != CampaignStatus.Active)
        {
            throw new MarketingRuleException("Only an active campaign can be paused.");
        }

        Status = CampaignStatus.Paused;
        UpdatedAt = now;
    }

    public void End(DateTimeOffset now)
    {
        Status = CampaignStatus.Ended;
        UpdatedAt = now;
    }

    public bool IsLive(DateTimeOffset now) =>
        Status == CampaignStatus.Active
        && (StartsAt is null || now >= StartsAt)
        && (EndsAt is null || now < EndsAt);

    /// <summary>
    /// Reduce a possibly-hostile campaign id to a safe slug (mt5_1/mt5_2): lower-case, only
    /// <c>[a-z0-9-_]</c>, trimmed and length-capped; returns null when nothing usable remains. Never
    /// throws — an invalid/external <c>cid</c> on a page-load path must degrade, not break (mt5_2 GOTCHA).
    /// </summary>
    public static string? SanitizeCid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var cleaned = new string(raw.Trim().ToLowerInvariant()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray()).Trim('-', '_');

        if (cleaned.Length == 0)
        {
            return null;
        }

        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }
}
