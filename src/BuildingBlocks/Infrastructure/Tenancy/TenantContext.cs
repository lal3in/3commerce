namespace ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// The ambient tenant context for one unit of work (ADR-0023/0024): the selected tenant, the
/// acting principal, and whether the principal is a MasterGlobal platform operator. It is
/// derived by the Gateway from the resolved domain/session and propagated to services; it is
/// never taken from a client-supplied header.
/// </summary>
public sealed record TenantContext(Guid? TenantId, Guid? PrincipalId, bool IsPlatformAdmin)
{
    /// <summary>No context — RLS fails closed (no rows) under this (ADR-0024).</summary>
    public static readonly TenantContext None = new(null, null, false);

    public static TenantContext ForTenant(Guid tenantId, Guid? principalId = null) => new(tenantId, principalId, false);

    /// <summary>Explicit MasterGlobal cross-tenant scope — rare, and high-risk uses are audited.</summary>
    public static TenantContext Platform(Guid? principalId = null) => new(null, principalId, true);
}
