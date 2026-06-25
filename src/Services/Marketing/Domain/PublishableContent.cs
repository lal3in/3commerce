namespace ThreeCommerce.Marketing.Domain;

public enum PublishStatus { Draft = 1, Scheduled = 2, Published = 3 }

/// <summary>
/// An expiring, read-only preview pointer to a specific draft version (mt5_7). The storefront renders
/// the targeted version with noindex (GOTCHA: preview pages are noindex + read-only). Signing the link
/// reuses the shared SignedDownload HMAC at the endpoint; the domain models the target + expiry.
/// </summary>
public sealed record PreviewToken(Guid ContentId, int Version, DateTimeOffset ExpiresAt)
{
    public bool IsValid(DateTimeOffset now) => now < ExpiresAt;
}

/// <summary>
/// Versioned publishable content — e.g. a storefront page/template (mt5_7). Every save is a new draft
/// version (history retained for rollback); publishing snapshots the current draft as the live version.
/// A publish can be scheduled and is applied by a sweep (mt6_3 scheduler) when due. Publishing/rollback
/// is where a cache-revalidation event would fire (storefront ISR).
/// </summary>
public sealed class PublishableContent
{
    private readonly Dictionary<int, string> _versions = new();

    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string Key { get; init; }
    public PublishStatus Status { get; private set; } = PublishStatus.Draft;
    public int DraftVersion { get; private set; }
    public int? PublishedVersion { get; private set; }
    public DateTimeOffset? ScheduledAt { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public string DraftPayload => _versions[DraftVersion];
    public string? PublishedPayload => PublishedVersion is { } v ? _versions[v] : null;
    public IReadOnlyCollection<int> Versions => _versions.Keys;

    private PublishableContent() { }

    public static PublishableContent Create(Guid tenantId, string key, string payload, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new MarketingRuleException("Content needs a key.");
        }

        var content = new PublishableContent { Id = Guid.CreateVersion7(), TenantId = tenantId, Key = key.Trim(), CreatedAt = now, UpdatedAt = now };
        content._versions[1] = payload;
        content.DraftVersion = 1;
        return content;
    }

    /// <summary>Save a new draft version (history retained). Clears any pending schedule.</summary>
    public void SaveDraft(string payload, DateTimeOffset now)
    {
        DraftVersion++;
        _versions[DraftVersion] = payload;
        Status = PublishStatus.Draft;
        ScheduledAt = null;
        UpdatedAt = now;
    }

    public PreviewToken Preview(DateTimeOffset now, TimeSpan validFor) =>
        new(Id, DraftVersion, now + validFor);

    public void Publish(DateTimeOffset now)
    {
        PublishedVersion = DraftVersion;
        Status = PublishStatus.Published;
        ScheduledAt = null;
        UpdatedAt = now;
    }

    public void SchedulePublish(DateTimeOffset at, DateTimeOffset now)
    {
        if (at <= now)
        {
            throw new MarketingRuleException("A scheduled publish must be in the future.");
        }

        Status = PublishStatus.Scheduled;
        ScheduledAt = at;
        UpdatedAt = now;
    }

    /// <summary>Publish if a scheduled time has arrived (mt6_3 sweep). Returns true when it publishes.</summary>
    public bool PublishDueScheduled(DateTimeOffset now)
    {
        if (Status == PublishStatus.Scheduled && ScheduledAt is { } at && now >= at)
        {
            Publish(now);
            return true;
        }

        return false;
    }

    public void Rollback(int toVersion, DateTimeOffset now)
    {
        if (!_versions.ContainsKey(toVersion))
        {
            throw new MarketingRuleException($"Version {toVersion} does not exist.");
        }

        PublishedVersion = toVersion;
        Status = PublishStatus.Published;
        ScheduledAt = null;
        UpdatedAt = now;
    }
}
