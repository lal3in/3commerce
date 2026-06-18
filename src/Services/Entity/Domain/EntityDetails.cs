namespace ThreeCommerce.Entity.Domain;

public sealed class EntityAddress
{
    public Guid Id { get; init; }
    public Guid EntityId { get; init; }
    public EntityAddressPurpose Purpose { get; init; }
    public int Version { get; init; }
    public required string Line1 { get; init; }
    public string? Line2 { get; init; }
    public required string City { get; init; }
    public string? Region { get; init; }
    public required string Postcode { get; init; }
    public required string CountryCode { get; init; }
    public bool IsCurrent { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? SupersededAt { get; private set; }

    public void Supersede(DateTimeOffset now)
    {
        IsCurrent = false;
        SupersededAt ??= now;
    }
}

public sealed class EntityIdentifier
{
    public Guid Id { get; init; }
    public Guid EntityId { get; init; }
    public EntityIdentifierType Type { get; init; }
    public required string Value { get; init; }
    public EntityVerificationStatus VerificationStatus { get; set; } = EntityVerificationStatus.Unverified;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

public sealed class EntityContactMethod
{
    public Guid Id { get; init; }
    public Guid EntityId { get; init; }
    public EntityContactPurpose Purpose { get; init; }
    public EntityContactKind Kind { get; init; }
    public required string Value { get; init; }
    public EntityVerificationStatus VerificationStatus { get; set; } = EntityVerificationStatus.Unverified;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

public sealed class EntityRelationship
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid FromEntityId { get; init; }
    public Guid ToEntityId { get; init; }
    public EntityRelationshipType Type { get; init; }
    public DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; private set; }

    private EntityRelationship()
    {
    }

    public static EntityRelationship Create(
        Guid tenantId,
        Guid fromEntityId,
        Guid toEntityId,
        EntityRelationshipType type,
        DateTimeOffset effectiveFrom)
    {
        if (tenantId == Guid.Empty || fromEntityId == Guid.Empty || toEntityId == Guid.Empty)
        {
            throw new DomainRuleException("Relationship tenant and entity IDs are required.");
        }

        if (fromEntityId == toEntityId)
        {
            throw new DomainRuleException("An entity cannot relate to itself.");
        }

        if (!Enum.IsDefined(type))
        {
            throw new DomainRuleException($"Unknown relationship type '{type}'.");
        }

        return new EntityRelationship
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            FromEntityId = fromEntityId,
            ToEntityId = toEntityId,
            Type = type,
            EffectiveFrom = effectiveFrom,
        };
    }

    public void End(DateTimeOffset now)
    {
        if (EffectiveTo is not null)
        {
            return;
        }

        EffectiveTo = now;
    }
}

public enum EntityAddressPurpose
{
    RegisteredOffice = 1,
    Billing = 2,
    Shipping = 3,
    Warehouse = 4,
    Returns = 5,
    Mailing = 6,
}

public enum EntityIdentifierType
{
    Abn = 1,
    Acn = 2,
    Gst = 3,
    OtherTaxRegistration = 4,
}

public enum EntityVerificationStatus
{
    Unverified = 1,
    Pending = 2,
    Verified = 3,
    Failed = 4,
}

public enum EntityContactPurpose
{
    Primary = 1,
    Accounts = 2,
    Operations = 3,
    Support = 4,
    Legal = 5,
}

public enum EntityContactKind
{
    Email = 1,
    Phone = 2,
    Website = 3,
}

public enum EntityRelationshipType
{
    Owns = 1,
    EmployeeOf = 2,
    SupplierFor = 3,
    OperatesWarehouseFor = 4,
    CourierFor = 5,
    BillingContactFor = 6,
}
