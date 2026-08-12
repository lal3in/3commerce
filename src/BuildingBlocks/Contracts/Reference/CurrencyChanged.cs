namespace ThreeCommerce.BuildingBlocks.Contracts.Reference;

/// <summary>
/// A tenant's currency registry entry was created, edited, enabled or disabled (currency_1). Published by
/// the Entity service (the reference/master-data owner) on every mutation; services that validate or format
/// money (Catalog, Ordering, Admin) project it into a local read model rather than reading Entity's DB.
/// <see cref="Enabled"/> false means the code is forward-only retired — usable in history, not for new use.
/// </summary>
public record CurrencyChanged(
    Guid TenantId,
    string Code,
    string Name,
    string Symbol,
    int DecimalPlaces,
    bool Enabled);
