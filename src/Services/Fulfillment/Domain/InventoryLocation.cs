namespace ThreeCommerce.Fulfillment.Domain;

/// <summary>Where stock physically lives and ships from (ADR-0003 fulfillment sources).</summary>
public enum LocationKind
{
    /// <summary>A warehouse the tenant operates directly.</summary>
    TenantWarehouse = 1,

    /// <summary>A supplier that drop-ships on the tenant's behalf.</summary>
    SupplierDirect = 2,

    /// <summary>A 3PL / forwarder holding stock for the tenant.</summary>
    ThirdPartyForwarder = 3,
}

public enum LocationStatus { Active = 1, Inactive = 2 }

/// <summary>
/// A stock-holding / shipping origin, linked to an owning Entity (warehouse, supplier, or
/// forwarder) and one of its versioned addresses. Tenant-scoped (ADR-0023).
/// </summary>
public sealed class InventoryLocation
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    /// <summary>The owning party in the Entity service (warehouse/supplier/forwarder).</summary>
    public Guid EntityId { get; init; }

    /// <summary>The owning entity's address this location ships from (versioned in Entity), if known.</summary>
    public Guid? AddressId { get; init; }

    public required string Name { get; set; }

    public LocationKind Kind { get; init; }

    public LocationStatus Status { get; private set; } = LocationStatus.Active;

    public DateTimeOffset CreatedAt { get; init; }

    public bool IsActive => Status == LocationStatus.Active;

    private InventoryLocation() { }

    public static InventoryLocation Create(
        Guid tenantId, Guid entityId, Guid? addressId, string name, LocationKind kind, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new FulfillmentRuleException("TenantId is required.");
        }

        if (entityId == Guid.Empty)
        {
            throw new FulfillmentRuleException("A location must be linked to an owning entity.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new FulfillmentRuleException("Location name is required.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new FulfillmentRuleException($"Unknown location kind '{kind}'.");
        }

        return new InventoryLocation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            EntityId = entityId,
            AddressId = addressId,
            Name = name.Trim(),
            Kind = kind,
            CreatedAt = now,
        };
    }

    public void Deactivate() => Status = LocationStatus.Inactive;

    public void Activate() => Status = LocationStatus.Active;
}
