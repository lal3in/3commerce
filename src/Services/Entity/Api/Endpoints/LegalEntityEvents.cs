using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Entity;
using ThreeCommerce.BuildingBlocks.Contracts.Tax;
using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Api.Endpoints;

/// <summary>
/// Builds and publishes <see cref="LegalEntityChanged"/> (ADR-0063 P2). Publish BEFORE SaveChanges so the
/// message lands in the same bus-outbox transaction as the mutation and its audit entry. The entity must be
/// loaded with its profiles, identifiers, addresses and establishments (<c>LoadWithDetailsAsync</c>).
/// </summary>
public static class LegalEntityEvents
{
    /// <summary>Publishes the snapshot only when <paramref name="entity"/> is its tenant's seller of record.</summary>
    public static Task PublishIfSellerAsync(IPublishEndpoint publish, EntityRecord entity, DateTimeOffset now, CancellationToken ct) =>
        entity.IsSeller ? publish.Publish(ToSnapshot(entity, now), ct) : Task.CompletedTask;

    public static LegalEntityChanged ToSnapshot(EntityRecord entity, DateTimeOffset now)
    {
        var office = entity.Addresses
            .Where(a => a.IsCurrent && a.Purpose == EntityAddressPurpose.RegisteredOffice)
            .OrderByDescending(a => a.Version)
            .FirstOrDefault();
        return new LegalEntityChanged(
            entity.TenantId,
            entity.Id,
            entity.LegalName,
            entity.TradingName,
            entity.IsSeller,
            [.. entity.Identifiers.OrderBy(i => i.Type).ThenBy(i => i.Value).Select(i => new LegalEntityTaxIdentifier(
                i.Id, (TaxIdScheme)(int)i.Type, i.Value, i.EffectiveJurisdiction, i.VerificationStatus == EntityVerificationStatus.Verified))],
            [.. entity.Establishments.OrderBy(e => e.Key).Select(e => new LegalEntityEstablishment(
                e.Id, e.Key, e.Label, e.Jurisdiction,
                new LegalEntityAddress(e.Line1, e.Line2, e.City, e.Region, e.Postcode, e.CountryCode)))],
            office is null ? null : new LegalEntityAddress(office.Line1, office.Line2, office.City, office.Region, office.Postcode, office.CountryCode),
            now);
    }
}
