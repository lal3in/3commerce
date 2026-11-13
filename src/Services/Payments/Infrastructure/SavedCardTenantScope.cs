using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// The tenant scope every read and write of <c>payments."SavedPaymentMethods"</c> runs in (ADR-0024, owner decision
/// 2026-11-07, mem_2). The table is under FORCE ROW LEVEL SECURITY: with no tenant set, a read returns no rows and an
/// insert fails <c>WITH CHECK</c>. So each caller passes the tenant it already holds — the request's <c>tenant</c> claim,
/// or the tenant on the row or message a background consumer is working from.
/// <list type="bullet">
/// <item>No transaction open (an endpoint, <c>SubscriptionService.PayAsync</c>): the work runs in its own tenant-scoped
/// transaction (<see cref="TenantDatabaseExtensions.RunInTenantScopeAsync{T}"/>), which commits when it returns, so a
/// write's <c>SaveChangesAsync</c> must happen inside <paramref name="work"/>.</item>
/// <item>A transaction already open (a consumer inside MassTransit's EF outbox transaction): the same transaction-local
/// settings are applied to THAT transaction. They end with it, so a pooled connection still never carries them on.</item>
/// <item>A non-relational provider (EF InMemory in unit tests) has no RLS and no transactions: the work just runs.</item>
/// </list>
/// </summary>
public static class SavedCardTenantScope
{
    private const string SetScopeSql =
        "SELECT set_config('app.tenant_id', {0}, true), " +
        "set_config('app.principal_id', {1}, true), " +
        "set_config('app.is_platform_admin', 'false', true)";

    public static async Task<T> RunAsync<T>(
        PaymentsDbContext db, Guid tenantId, Func<Task<T>> work, CancellationToken ct, Guid? principalId = null)
    {
        if (!db.Database.IsRelational())
        {
            return await work();
        }

        if (db.Database.CurrentTransaction is null)
        {
            return await db.RunInTenantScopeAsync(TenantContext.ForTenant(tenantId, principalId), work, ct);
        }

        await db.Database.ExecuteSqlRawAsync(
            SetScopeSql, [tenantId.ToString(), principalId?.ToString() ?? string.Empty], ct);
        return await work();
    }

    public static Task RunAsync(
        PaymentsDbContext db, Guid tenantId, Func<Task> work, CancellationToken ct, Guid? principalId = null) =>
        RunAsync(db, tenantId, async () => { await work(); return true; }, ct, principalId);
}
