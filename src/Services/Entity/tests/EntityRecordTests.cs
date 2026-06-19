using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Tests;

public class EntityRecordTests
{
    [Fact]
    public void Create_requires_tenant_id()
    {
        var ex = Assert.Throws<DomainRuleException>(() => EntityRecord.Create(Guid.Empty, "Acme", DateTimeOffset.UtcNow));
        Assert.Contains("TenantId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityModel_create_normalizes_names_and_display_name()
    {
        var now = DateTimeOffset.UtcNow;
        var record = EntityRecord.Create(
            Guid.CreateVersion7(),
            EntityType.Company,
            "  Acme Pty Ltd  ",
            "  Acme Wholesale  ",
            now,
            [EntityRoleKind.Supplier, EntityRoleKind.PaymentRecipient]);

        Assert.Equal(EntityType.Company, record.Type);
        Assert.Equal("Acme Pty Ltd", record.LegalName);
        Assert.Equal("Acme Wholesale", record.TradingName);
        Assert.Equal("Acme Wholesale", record.DisplayName);
        Assert.Equal(EntityRecordStatus.Active, record.Status);
        Assert.Equal(now, record.CreatedAt);
        Assert.Equal(now, record.UpdatedAt);
        Assert.Equal([EntityRoleKind.Supplier, EntityRoleKind.PaymentRecipient], record.Profiles.Select(p => p.Role).ToArray());
    }

    [Theory]
    [InlineData(EntityType.NaturalPerson)]
    [InlineData(EntityType.Company)]
    [InlineData(EntityType.Trust)]
    [InlineData(EntityType.Partnership)]
    [InlineData(EntityType.SoleTrader)]
    [InlineData(EntityType.GovernmentBody)]
    [InlineData(EntityType.NonProfitAssociation)]
    [InlineData(EntityType.Other)]
    public void EntityModel_supports_required_entity_types(EntityType type)
    {
        var record = EntityRecord.Create(Guid.CreateVersion7(), type, "Valid Name", null, DateTimeOffset.UtcNow, []);

        Assert.Equal(type, record.Type);
    }

    [Fact]
    public void EntityModel_add_profile_is_idempotent()
    {
        var record = EntityRecord.Create(Guid.CreateVersion7(), EntityType.Trust, "Family Trust", null, DateTimeOffset.UtcNow, []);

        var first = record.AddProfile(EntityRoleKind.Supplier, DateTimeOffset.UtcNow);
        var second = record.AddProfile(EntityRoleKind.Supplier, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.Same(first, second);
        Assert.Single(record.Profiles);
    }

    [Fact]
    public void Archive_is_idempotent()
    {
        var record = EntityRecord.Create(Guid.CreateVersion7(), "Acme Pty Ltd", DateTimeOffset.UtcNow);
        var archivedAt = DateTimeOffset.UtcNow.AddMinutes(5);

        record.Archive(archivedAt);
        record.Archive(archivedAt.AddMinutes(1));

        Assert.Equal(EntityRecordStatus.Archived, record.Status);
        Assert.Equal(archivedAt, record.UpdatedAt);
    }
}
