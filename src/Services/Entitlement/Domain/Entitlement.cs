using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Entitlement.Domain;

public enum EntitlementType { Subscription = 1, License = 2, Download = 3, ApiAccess = 4, ServiceAccess = 5 }

public enum EntitlementStatus { Active = 1, Expired = 2, Suspended = 3, Cancelled = 4 }

/// <summary>
/// What a customer receives for a digital/service line (Phase 7 / mt7_2): access issued when the line's
/// order confirms, instead of a shipment. Owned by the dedicated Entitlement service, which consumes
/// OrderConfirmed and issues access. Email-scoped (works for guests + registered customers).
/// </summary>
public sealed class Entitlement
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid OrderId { get; init; }
    public required string CustomerEmail { get; init; }
    public Guid ProductId { get; init; }
    public Guid? VariantId { get; init; }
    public EntitlementType Type { get; init; }
    public EntitlementStatus Status { get; private set; } = EntitlementStatus.Active;
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }

    public bool IsActive => Status == EntitlementStatus.Active;

    private Entitlement() { }

    /// <summary>Issue access for a confirmed digital/service line. Returns null for a non-digital line.</summary>
    public static Entitlement? Issue(
        Guid tenantId, Guid orderId, string email, Guid productId, Guid? variantId, FulfilmentType fulfilment, DateTimeOffset now)
    {
        var type = TypeFor(fulfilment);
        if (type is null)
        {
            return null;
        }

        return new Entitlement
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrderId = orderId,
            CustomerEmail = email.Trim().ToLowerInvariant(),
            ProductId = productId,
            VariantId = variantId,
            Type = type.Value,
            StartsAt = now,
            CreatedAt = now,
        };
    }

    public void Suspend() => Status = EntitlementStatus.Suspended;

    public void Cancel() => Status = EntitlementStatus.Cancelled;

    public void Expire(DateTimeOffset at)
    {
        Status = EntitlementStatus.Expired;
        ExpiresAt = at;
    }

    /// <summary>Maps the line's fulfilment type to an entitlement type; null = a physical (shipped) line.</summary>
    public static EntitlementType? TypeFor(FulfilmentType fulfilment) => fulfilment switch
    {
        FulfilmentType.DigitalDownload => EntitlementType.Download,
        FulfilmentType.Subscription => EntitlementType.Subscription,
        FulfilmentType.Usage => EntitlementType.ApiAccess,
        FulfilmentType.ManualService => EntitlementType.ServiceAccess,
        _ => null,
    };
}
