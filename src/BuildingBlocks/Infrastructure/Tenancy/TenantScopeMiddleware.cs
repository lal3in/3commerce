using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// Wraps each authenticated request in a tenant-scoped DB transaction (ADR-0024). The tenant + principal
/// come from the internal claims (never a client header); they are applied as transaction-local RLS
/// settings via <see cref="TenantDatabaseExtensions.BeginTenantScopeAsync"/>, the request runs, then the
/// transaction commits. Anonymous/health requests (no <c>tenant</c> claim) are not wrapped.
///
/// Required by any service whose tables use <c>FORCE ROW LEVEL SECURITY</c> and connects as its own
/// (non-superuser) role — without it, RLS fails closed and reads return no rows / writes are rejected.
/// </summary>
public sealed class TenantScopeMiddleware<TDbContext>(RequestDelegate next)
    where TDbContext : DbContext
{
    public async Task InvokeAsync(HttpContext context, TDbContext db, ITenantContextAccessor accessor)
    {
        if (!Guid.TryParse(context.User.FindFirstValue("tenant"), out var tenantId))
        {
            await next(context);
            return;
        }

        Guid? principalId = Guid.TryParse(context.User.FindFirstValue("sub"), out var sub) ? sub : null;
        var tenant = TenantContext.ForTenant(tenantId, principalId);
        accessor.Current = tenant;

        await using var tx = await db.BeginTenantScopeAsync(tenant, context.RequestAborted);
        await next(context);
        await tx.CommitAsync(context.RequestAborted);
    }
}

public static class TenantScopeExtensions
{
    /// <summary>Registers the ambient tenant-context accessor (AsyncLocal).</summary>
    public static IServiceCollection AddTenantContext(this IServiceCollection services)
    {
        services.AddSingleton<ITenantContextAccessor, AsyncLocalTenantContextAccessor>();
        return services;
    }

    /// <summary>Wraps authenticated requests in a tenant-scoped transaction for <typeparamref name="TDbContext"/>.
    /// Place after <c>UseAuthentication</c>/<c>UseAuthorization</c> so the claims are available.</summary>
    public static IApplicationBuilder UseTenantScope<TDbContext>(this IApplicationBuilder app)
        where TDbContext : DbContext =>
        app.UseMiddleware<TenantScopeMiddleware<TDbContext>>();
}
