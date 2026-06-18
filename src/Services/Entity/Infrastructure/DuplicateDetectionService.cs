using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Infrastructure;

public sealed class DuplicateDetectionService(EntityDbContext db, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<DuplicateWarning>> DetectForEntityAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var candidate = await db.Entities
            .Include(e => e.Identifiers)
            .Include(e => e.ContactMethods)
            .SingleAsync(e => e.Id == entityId, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var warnings = new List<DuplicateWarning>();

        await AddNameWarningsAsync(candidate, DuplicateWarningKind.LegalName, candidate.LegalName, warnings, now, cancellationToken);
        if (!string.IsNullOrWhiteSpace(candidate.TradingName))
        {
            await AddNameWarningsAsync(candidate, DuplicateWarningKind.TradingName, candidate.TradingName, warnings, now, cancellationToken);
        }

        foreach (var identifier in candidate.Identifiers)
        {
            var existingIds = await db.EntityIdentifiers.AsNoTracking()
                .Where(i => i.EntityId != candidate.Id && i.Type == identifier.Type && i.Value == identifier.Value)
                .Join(db.Entities, i => i.EntityId, e => e.Id, (i, e) => new { i.EntityId, e.TenantId })
                .Where(x => x.TenantId == candidate.TenantId)
                .Select(x => x.EntityId)
                .ToListAsync(cancellationToken);
            AddWarnings(candidate, existingIds, DuplicateWarningKind.Identifier, $"{identifier.Type}:{identifier.Value}", warnings, now);
        }

        foreach (var contact in candidate.ContactMethods)
        {
            var existingIds = await db.EntityContactMethods.AsNoTracking()
                .Where(c => c.EntityId != candidate.Id && c.Kind == contact.Kind && c.Value == contact.Value)
                .Join(db.Entities, c => c.EntityId, e => e.Id, (c, e) => new { c.EntityId, e.TenantId })
                .Where(x => x.TenantId == candidate.TenantId)
                .Select(x => x.EntityId)
                .ToListAsync(cancellationToken);
            AddWarnings(candidate, existingIds, DuplicateWarningKind.Contact, $"{contact.Kind}:{contact.Value}", warnings, now);
        }

        db.DuplicateWarnings.AddRange(warnings);
        await db.SaveChangesAsync(cancellationToken);
        return warnings;
    }

    public async Task OverrideAsync(Guid warningId, string reason, CancellationToken cancellationToken)
    {
        var warning = await db.DuplicateWarnings.SingleAsync(w => w.Id == warningId, cancellationToken);
        warning.Override(reason, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task AddNameWarningsAsync(
        EntityRecord candidate,
        DuplicateWarningKind kind,
        string value,
        List<DuplicateWarning> warnings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalized = value.Trim();
        var existingIds = await db.Entities.AsNoTracking()
            .Where(e => e.TenantId == candidate.TenantId && e.Id != candidate.Id)
            .Where(e => kind == DuplicateWarningKind.LegalName ? e.LegalName == normalized : e.TradingName == normalized)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
        AddWarnings(candidate, existingIds, kind, normalized, warnings, now);
    }

    private static void AddWarnings(
        EntityRecord candidate,
        IEnumerable<Guid> existingIds,
        DuplicateWarningKind kind,
        string matchedValue,
        List<DuplicateWarning> warnings,
        DateTimeOffset now)
    {
        foreach (var existingId in existingIds.Distinct())
        {
            warnings.Add(DuplicateWarning.Create(candidate.TenantId, candidate.Id, existingId, kind, matchedValue, now));
        }
    }
}
