namespace ThreeCommerce.Entity.Domain;

public sealed class EntityRecord
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public EntityType Type { get; private set; }
    public string LegalName { get; private set; } = string.Empty;
    public string? TradingName { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    public EntityRecordStatus Status { get; private set; } = EntityRecordStatus.Active;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public List<EntityProfile> Profiles { get; private set; } = [];
    public List<EntityAddress> Addresses { get; private set; } = [];
    public List<EntityIdentifier> Identifiers { get; private set; } = [];
    public List<EntityContactMethod> ContactMethods { get; private set; } = [];

    private EntityRecord()
    {
    }

    public static EntityRecord Create(Guid tenantId, string displayName, DateTimeOffset now) =>
        Create(tenantId, EntityType.Other, displayName, null, now, []);

    public static EntityRecord Create(
        Guid tenantId,
        EntityType type,
        string legalName,
        string? tradingName,
        DateTimeOffset now,
        IEnumerable<EntityRoleKind> roles)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainRuleException("TenantId is required.");
        }

        EnsureSupportedType(type);
        var normalizedLegalName = NormalizeName(legalName, nameof(LegalName));
        var normalizedTradingName = NormalizeOptionalName(tradingName, nameof(TradingName));
        var entity = new EntityRecord
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Type = type,
            LegalName = normalizedLegalName,
            TradingName = normalizedTradingName,
            DisplayName = normalizedTradingName ?? normalizedLegalName,
            CreatedAt = now,
            UpdatedAt = now,
        };

        foreach (var role in roles.Distinct())
        {
            entity.AddProfile(role, now);
        }

        return entity;
    }

    public void Rename(string displayName, DateTimeOffset now)
    {
        var normalizedName = NormalizeName(displayName, nameof(DisplayName));
        LegalName = normalizedName;
        TradingName = null;
        DisplayName = normalizedName;
        UpdatedAt = now;
    }

    public void UpdateNames(string legalName, string? tradingName, DateTimeOffset now)
    {
        LegalName = NormalizeName(legalName, nameof(LegalName));
        TradingName = NormalizeOptionalName(tradingName, nameof(TradingName));
        DisplayName = TradingName ?? LegalName;
        UpdatedAt = now;
    }

    public EntityProfile AddProfile(EntityRoleKind role, DateTimeOffset now)
    {
        if (!Enum.IsDefined(role))
        {
            throw new DomainRuleException($"Unknown entity role '{role}'.");
        }

        var existing = Profiles.SingleOrDefault(p => p.Role == role && p.Status == EntityProfileStatus.Active);
        if (existing is not null)
        {
            return existing;
        }

        var profile = new EntityProfile
        {
            Id = Guid.CreateVersion7(),
            EntityId = Id,
            Role = role,
            Status = EntityProfileStatus.Active,
            CreatedAt = now,
        };
        Profiles.Add(profile);
        UpdatedAt = now;
        return profile;
    }

    public EntityAddress AddAddress(
        EntityAddressPurpose purpose,
        string line1,
        string? line2,
        string city,
        string? region,
        string postcode,
        string countryCode,
        DateTimeOffset now)
    {
        var nextVersion = Addresses.Where(a => a.Purpose == purpose).Select(a => a.Version).DefaultIfEmpty(0).Max() + 1;
        foreach (var current in Addresses.Where(a => a.Purpose == purpose && a.IsCurrent))
        {
            current.Supersede(now);
        }

        var address = new EntityAddress
        {
            Id = Guid.CreateVersion7(),
            EntityId = Id,
            Purpose = purpose,
            Version = nextVersion,
            Line1 = NormalizeName(line1, nameof(line1)),
            Line2 = NormalizeOptionalName(line2, nameof(line2)),
            City = NormalizeName(city, nameof(city)),
            Region = NormalizeOptionalName(region, nameof(region)),
            Postcode = NormalizeName(postcode, nameof(postcode)),
            CountryCode = NormalizeCountryCode(countryCode),
            CreatedAt = now,
        };
        Addresses.Add(address);
        UpdatedAt = now;
        return address;
    }

    public EntityIdentifier AddIdentifier(EntityIdentifierType type, string value, DateTimeOffset now)
    {
        var normalizedValue = NormalizeIdentifierValue(type, value);
        if (Identifiers.Any(i => i.Type == type && i.Value == normalizedValue))
        {
            throw new DomainRuleException($"Identifier '{type}' already exists on this entity.");
        }

        var identifier = new EntityIdentifier
        {
            Id = Guid.CreateVersion7(),
            EntityId = Id,
            Type = type,
            Value = normalizedValue,
            CreatedAt = now,
        };
        Identifiers.Add(identifier);
        UpdatedAt = now;
        return identifier;
    }

    public EntityContactMethod AddContactMethod(EntityContactPurpose purpose, EntityContactKind kind, string value, DateTimeOffset now)
    {
        var normalizedValue = NormalizeContactValue(kind, value);
        var contact = new EntityContactMethod
        {
            Id = Guid.CreateVersion7(),
            EntityId = Id,
            Purpose = purpose,
            Kind = kind,
            Value = normalizedValue,
            CreatedAt = now,
        };
        ContactMethods.Add(contact);
        UpdatedAt = now;
        return contact;
    }

    public void Archive(DateTimeOffset now)
    {
        if (Status == EntityRecordStatus.Archived)
        {
            return;
        }

        Status = EntityRecordStatus.Archived;
        UpdatedAt = now;
    }

    private static void EnsureSupportedType(EntityType type)
    {
        if (!Enum.IsDefined(type))
        {
            throw new DomainRuleException($"Unknown entity type '{type}'.");
        }
    }

    private static string NormalizeName(string value, string fieldName)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 2 or > 200)
        {
            throw new DomainRuleException($"{fieldName} must be between 2 and 200 characters.");
        }

        return normalized;
    }

    private static string NormalizeCountryCode(string countryCode)
    {
        var value = countryCode.Trim().ToUpperInvariant();
        if (value.Length != 2 || !value.All(char.IsAsciiLetter))
        {
            throw new DomainRuleException("CountryCode must be ISO 3166-1 alpha-2.");
        }

        return value;
    }

    private static string NormalizeIdentifierValue(EntityIdentifierType type, string value)
    {
        var normalized = type is EntityIdentifierType.Abn or EntityIdentifierType.Acn
            ? new string(value.Where(char.IsAsciiDigit).ToArray())
            : value.Trim().ToUpperInvariant();

        var valid = type switch
        {
            EntityIdentifierType.Abn => normalized.Length == 11,
            EntityIdentifierType.Acn => normalized.Length == 9,
            _ => normalized.Length is >= 2 and <= 80,
        };

        if (!valid)
        {
            throw new DomainRuleException($"Invalid {type} identifier value.");
        }

        return normalized;
    }

    private static string NormalizeContactValue(EntityContactKind kind, string value)
    {
        var normalized = value.Trim();
        if (kind == EntityContactKind.Email)
        {
            normalized = normalized.ToLowerInvariant();
        }

        if (normalized.Length is < 3 or > 320)
        {
            throw new DomainRuleException($"Invalid {kind} contact value.");
        }

        return normalized;
    }

    private static string? NormalizeOptionalName(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeName(value, fieldName);
    }
}

public sealed class EntityProfile
{
    public Guid Id { get; init; }
    public Guid EntityId { get; init; }
    public EntityRoleKind Role { get; init; }
    public EntityProfileStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

public enum EntityType
{
    NaturalPerson = 1,
    Company = 2,
    Trust = 3,
    Partnership = 4,
    SoleTrader = 5,
    GovernmentBody = 6,
    NonProfitAssociation = 7,
    Other = 99,
}

public enum EntityRoleKind
{
    Customer = 1,
    Supplier = 2,
    Courier = 3,
    Warehouse = 4,
    Forwarder = 5,
    TenantOwner = 6,
    PaymentRecipient = 7,
}

public enum EntityProfileStatus
{
    Active = 1,
    Archived = 2,
}

public enum EntityRecordStatus
{
    Active = 1,
    Archived = 2,
}
