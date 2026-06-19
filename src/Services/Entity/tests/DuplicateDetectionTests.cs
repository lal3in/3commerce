using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Entity.Domain;
using ThreeCommerce.Entity.Infrastructure;

namespace ThreeCommerce.Entity.Tests;

public class DuplicateDetectionTests
{
    [Fact]
    public async Task Duplicate_warns_on_same_tenant_legal_name()
    {
        await using var db = NewDb();
        var tenantId = Guid.CreateVersion7();
        var existing = EntityRecord.Create(tenantId, EntityType.Company, "Acme Pty Ltd", null, DateTimeOffset.UtcNow, []);
        var candidate = EntityRecord.Create(tenantId, EntityType.Company, "Acme Pty Ltd", null, DateTimeOffset.UtcNow, []);
        db.Entities.AddRange(existing, candidate);
        await db.SaveChangesAsync();

        var warnings = await new DuplicateDetectionService(db, TimeProvider.System).DetectForEntityAsync(candidate.Id, default);

        var warning = Assert.Single(warnings);
        Assert.Equal(DuplicateWarningKind.LegalName, warning.Kind);
        Assert.Equal(existing.Id, warning.ExistingEntityId);
        Assert.Equal(candidate.Id, warning.CandidateEntityId);
    }

    [Fact]
    public async Task Duplicate_ignores_same_name_in_different_tenant()
    {
        await using var db = NewDb();
        db.Entities.Add(EntityRecord.Create(Guid.CreateVersion7(), EntityType.Company, "Acme Pty Ltd", null, DateTimeOffset.UtcNow, []));
        var candidate = EntityRecord.Create(Guid.CreateVersion7(), EntityType.Company, "Acme Pty Ltd", null, DateTimeOffset.UtcNow, []);
        db.Entities.Add(candidate);
        await db.SaveChangesAsync();

        var warnings = await new DuplicateDetectionService(db, TimeProvider.System).DetectForEntityAsync(candidate.Id, default);

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Duplicate_warns_on_identifier_and_contact()
    {
        await using var db = NewDb();
        var tenantId = Guid.CreateVersion7();
        var existing = EntityRecord.Create(tenantId, EntityType.Company, "Acme Pty Ltd", null, DateTimeOffset.UtcNow, []);
        existing.AddIdentifier(EntityIdentifierType.Abn, "12345678901", DateTimeOffset.UtcNow);
        existing.AddContactMethod(EntityContactPurpose.Accounts, EntityContactKind.Email, "accounts@example.com", DateTimeOffset.UtcNow);
        var candidate = EntityRecord.Create(tenantId, EntityType.Company, "Other Pty Ltd", null, DateTimeOffset.UtcNow, []);
        candidate.AddIdentifier(EntityIdentifierType.Abn, "12 345 678 901", DateTimeOffset.UtcNow);
        candidate.AddContactMethod(EntityContactPurpose.Accounts, EntityContactKind.Email, "ACCOUNTS@example.com", DateTimeOffset.UtcNow);
        db.Entities.AddRange(existing, candidate);
        await db.SaveChangesAsync();

        var warnings = await new DuplicateDetectionService(db, TimeProvider.System).DetectForEntityAsync(candidate.Id, default);

        Assert.Contains(warnings, w => w.Kind == DuplicateWarningKind.Identifier && w.MatchedValue == "Abn:12345678901");
        Assert.Contains(warnings, w => w.Kind == DuplicateWarningKind.Contact && w.MatchedValue == "Email:accounts@example.com");
    }

    [Fact]
    public async Task Duplicate_override_requires_reason_and_sets_status()
    {
        await using var db = NewDb();
        var warning = DuplicateWarning.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            DuplicateWarningKind.LegalName,
            "Acme Pty Ltd",
            DateTimeOffset.UtcNow);
        db.DuplicateWarnings.Add(warning);
        await db.SaveChangesAsync();

        await new DuplicateDetectionService(db, TimeProvider.System).OverrideAsync(warning.Id, "Known related entity", default);

        var reloaded = await db.DuplicateWarnings.SingleAsync(w => w.Id == warning.Id);
        Assert.Equal(DuplicateWarningStatus.Overridden, reloaded.Status);
        Assert.Equal("Known related entity", reloaded.OverrideReason);
    }

    private static EntityDbContext NewDb() => new(new DbContextOptionsBuilder<EntityDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);
}
