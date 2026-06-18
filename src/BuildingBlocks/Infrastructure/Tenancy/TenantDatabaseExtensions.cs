using Microsoft.EntityFrameworkCore;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// Runs a unit of work inside a transaction with the tenant context applied as transaction-local
/// settings (ADR-0024). PostgreSQL RLS policies read these via <c>current_setting()</c>; using
/// <c>set_config(..., is_local =&gt; true)</c> scopes them to the transaction, so a pooled
/// connection can never carry one tenant's context into the next request. No context set =&gt; no
/// rows (fail closed). Tenant tables must use <c>FORCE ROW LEVEL SECURITY</c> because the service
/// connects as the table owner, who would otherwise bypass RLS.
/// </summary>
public static class TenantDatabaseExtensions
{
    private const string SetScopeSql =
        "SELECT set_config('app.tenant_id', {0}, true), " +
        "set_config('app.principal_id', {1}, true), " +
        "set_config('app.is_platform_admin', {2}, true)";

    public static async Task<T> RunInTenantScopeAsync<T>(
        this DbContext db, TenantContext context, Func<Task<T>> work, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlRawAsync(
            SetScopeSql,
            [
                context.TenantId?.ToString() ?? string.Empty,
                context.PrincipalId?.ToString() ?? string.Empty,
                context.IsPlatformAdmin ? "true" : "false",
            ],
            ct);

        var result = await work();
        await tx.CommitAsync(ct);
        return result;
    }

    public static Task RunInTenantScopeAsync(
        this DbContext db, TenantContext context, Func<Task> work, CancellationToken ct = default) =>
        db.RunInTenantScopeAsync(context, async () => { await work(); return true; }, ct);
}
