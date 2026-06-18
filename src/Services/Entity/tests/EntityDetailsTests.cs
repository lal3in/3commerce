using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Tests;

public class EntityDetailsTests
{
    [Fact]
    public void EntityDetails_address_versions_are_immutable_and_superseded()
    {
        var entity = NewEntity();
        var first = entity.AddAddress(EntityAddressPurpose.RegisteredOffice, "1 First St", null, "Sydney", "NSW", "2000", "au", DateTimeOffset.UtcNow);
        var second = entity.AddAddress(EntityAddressPurpose.RegisteredOffice, "2 Second St", null, "Sydney", "NSW", "2001", "AU", DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.False(first.IsCurrent);
        Assert.NotNull(first.SupersededAt);
        Assert.True(second.IsCurrent);
        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.Equal("AU", second.CountryCode);
    }

    [Theory]
    [InlineData(EntityIdentifierType.Abn, "12 345 678 901", "12345678901")]
    [InlineData(EntityIdentifierType.Acn, "123 456 789", "123456789")]
    [InlineData(EntityIdentifierType.Gst, "gst-registered", "GST-REGISTERED")]
    public void EntityDetails_identifiers_are_normalized(EntityIdentifierType type, string input, string expected)
    {
        var identifier = NewEntity().AddIdentifier(type, input, DateTimeOffset.UtcNow);

        Assert.Equal(expected, identifier.Value);
        Assert.Equal(EntityVerificationStatus.Unverified, identifier.VerificationStatus);
    }

    [Fact]
    public void EntityDetails_duplicate_identifier_on_same_entity_is_rejected()
    {
        var entity = NewEntity();
        entity.AddIdentifier(EntityIdentifierType.Abn, "12345678901", DateTimeOffset.UtcNow);

        Assert.Throws<DomainRuleException>(() => entity.AddIdentifier(EntityIdentifierType.Abn, "12 345 678 901", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EntityDetails_contact_email_is_normalized()
    {
        var contact = NewEntity().AddContactMethod(EntityContactPurpose.Accounts, EntityContactKind.Email, " Accounts@Example.COM ", DateTimeOffset.UtcNow);

        Assert.Equal("accounts@example.com", contact.Value);
        Assert.Equal(EntityVerificationStatus.Unverified, contact.VerificationStatus);
    }

    [Fact]
    public void EntityDetails_relationships_cannot_self_reference()
    {
        var id = Guid.CreateVersion7();

        Assert.Throws<DomainRuleException>(() => EntityRelationship.Create(
            Guid.CreateVersion7(),
            id,
            id,
            EntityRelationshipType.SupplierFor,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EntityDetails_relationship_end_is_idempotent()
    {
        var relationship = EntityRelationship.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            EntityRelationshipType.SupplierFor,
            DateTimeOffset.UtcNow);
        var endedAt = DateTimeOffset.UtcNow.AddDays(1);

        relationship.End(endedAt);
        relationship.End(endedAt.AddDays(1));

        Assert.Equal(endedAt, relationship.EffectiveTo);
    }

    private static EntityRecord NewEntity() => EntityRecord.Create(
        Guid.CreateVersion7(),
        EntityType.Company,
        "Acme Pty Ltd",
        null,
        DateTimeOffset.UtcNow,
        []);
}
