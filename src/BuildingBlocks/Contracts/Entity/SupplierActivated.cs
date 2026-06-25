namespace ThreeCommerce.BuildingBlocks.Contracts.Entity;

public sealed record SupplierActivated(
    Guid SupplierOnboardingId,
    Guid EntityId,
    Guid TenantId,
    DateTimeOffset OccurredAt);
