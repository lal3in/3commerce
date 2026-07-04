using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Marketing.Domain;

namespace ThreeCommerce.Marketing.Infrastructure;

/// <summary>EF row for a <see cref="PublishableContent"/> aggregate (def_5). Versions live in child rows.</summary>
public class ContentRecord
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string Key { get; init; }
    public PublishStatus Status { get; set; }
    public int DraftVersion { get; set; }
    public int? PublishedVersion { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ContentVersionRecord> Versions { get; init; } = [];
}

/// <summary>One immutable draft/published version's payload — history is retained for rollback (mt5_7).</summary>
public class ContentVersionRecord
{
    public Guid Id { get; init; }
    public Guid ContentRecordId { get; init; }
    public int Version { get; init; }
    public required string Payload { get; init; }
}

/// <summary>
/// Persistence for the tested <see cref="PublishableContent"/> aggregate (def_5 / mt5_7): the domain
/// stays authoritative — this maps rows ↔ aggregate via <see cref="PublishableContent.Rehydrate"/>,
/// persists new versions append-only, and runs the due-scheduled sweep the Quartz job fires.
/// </summary>
public sealed class PublishingService(MarketingDbContext db, TimeProvider time)
{
    public Task<List<ContentRecord>> ListAsync(Guid tenantId, CancellationToken ct) =>
        db.Contents.AsNoTracking().Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Key).ToListAsync(ct);

    public async Task<PublishableContent?> GetAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        var record = await db.Contents.Include(c => c.Versions)
            .SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Id == id, ct);
        return record is null ? null : ToDomain(record);
    }

    /// <summary>Published payload for storefront rendering — by key, published only.</summary>
    public async Task<(string Key, int Version, string Payload)?> GetPublishedAsync(Guid tenantId, string key, CancellationToken ct)
    {
        var record = await db.Contents.AsNoTracking().Include(c => c.Versions)
            .SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Key == key && c.PublishedVersion != null, ct);
        if (record is null)
        {
            return null;
        }

        var version = record.PublishedVersion!.Value;
        return (record.Key, version, record.Versions.Single(v => v.Version == version).Payload);
    }

    /// <summary>Draft/any-version payload for the signed preview route (noindex, read-only).</summary>
    public async Task<string?> GetVersionPayloadAsync(Guid contentId, int version, CancellationToken ct) =>
        await db.Set<ContentVersionRecord>().AsNoTracking()
            .Where(v => v.ContentRecordId == contentId && v.Version == version)
            .Select(v => v.Payload)
            .SingleOrDefaultAsync(ct);

    public async Task<PublishableContent> CreateAsync(Guid tenantId, string key, string payload, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var normalizedKey = key.Trim();
        if (await db.Contents.AnyAsync(c => c.TenantId == tenantId && c.Key == normalizedKey, ct))
        {
            throw new MarketingRuleException($"Content with key '{normalizedKey}' already exists.");
        }

        var content = PublishableContent.Create(tenantId, normalizedKey, payload, now);
        db.Contents.Add(new ContentRecord
        {
            Id = content.Id,
            TenantId = content.TenantId,
            Key = content.Key,
            Status = content.Status,
            DraftVersion = content.DraftVersion,
            CreatedAt = content.CreatedAt,
            UpdatedAt = content.UpdatedAt,
            Versions = { new ContentVersionRecord { Id = Guid.CreateVersion7(), ContentRecordId = content.Id, Version = 1, Payload = payload } },
        });
        await db.SaveChangesAsync(ct);
        return content;
    }

    /// <summary>Load → run a domain mutation → persist (scalar fields + any new versions, append-only).</summary>
    public async Task<PublishableContent?> MutateAsync(Guid tenantId, Guid id, Action<PublishableContent> mutate, CancellationToken ct)
    {
        var record = await db.Contents.Include(c => c.Versions)
            .SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Id == id, ct);
        if (record is null)
        {
            return null;
        }

        var content = ToDomain(record);
        mutate(content);
        Persist(record, content);
        await db.SaveChangesAsync(ct);
        return content;
    }

    /// <summary>Publishes everything whose scheduled time has arrived (the mt6_3 sweep). Returns the count.</summary>
    public async Task<int> SweepDueScheduledAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = await db.Contents.Include(c => c.Versions)
            .Where(c => c.Status == PublishStatus.Scheduled && c.ScheduledAt != null && c.ScheduledAt <= now)
            .ToListAsync(ct);
        var published = 0;
        foreach (var record in due)
        {
            var content = ToDomain(record);
            if (content.PublishDueScheduled(now))
            {
                Persist(record, content);
                published++;
            }
        }

        await db.SaveChangesAsync(ct);
        return published;
    }

    private void Persist(ContentRecord record, PublishableContent content)
    {
        record.Status = content.Status;
        record.DraftVersion = content.DraftVersion;
        record.PublishedVersion = content.PublishedVersion;
        record.ScheduledAt = content.ScheduledAt;
        record.UpdatedAt = content.UpdatedAt;
        foreach (var version in content.Versions.Where(v => record.Versions.All(r => r.Version != v)))
        {
            // Added via the context, NOT record.Versions: a client-keyed entity reached through a
            // tracked parent's navigation is assumed Modified by DetectChanges → UPDATE affecting
            // 0 rows → DbUpdateConcurrencyException.
            db.Add(new ContentVersionRecord
            {
                Id = Guid.CreateVersion7(),
                ContentRecordId = record.Id,
                Version = version,
                Payload = version == content.DraftVersion ? content.DraftPayload : throw new MarketingRuleException("Only the draft can introduce a new version."),
            });
        }
    }

    private static PublishableContent ToDomain(ContentRecord record) =>
        PublishableContent.Rehydrate(
            record.Id, record.TenantId, record.Key, record.Status, record.DraftVersion, record.PublishedVersion,
            record.ScheduledAt, record.CreatedAt, record.UpdatedAt,
            record.Versions.ToDictionary(v => v.Version, v => v.Payload));
}
