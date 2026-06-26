using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Audit.Domain;
using ThreeCommerce.Audit.Infrastructure;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.Audit.Api.Endpoints;

/// <summary>Central audit search (mt6_1): cross-service projection, read-only.</summary>
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditSearch(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/audit", async (
                Guid? tenantId, string? resourceType, string? resourceId, string? action, string? outcome,
                AuditDbContext db, IConfiguration config, CancellationToken ct) =>
            {
                var tenant = tenantId ?? DefaultTenantId(config);
                var query = db.AuditEntries.AsNoTracking().Where(e => e.TenantId == tenant);
                if (!string.IsNullOrWhiteSpace(resourceType)) query = query.Where(e => e.ResourceType == resourceType);
                if (!string.IsNullOrWhiteSpace(resourceId)) query = query.Where(e => e.ResourceId == resourceId);
                if (!string.IsNullOrWhiteSpace(action)) query = query.Where(e => e.Action == action);
                if (!string.IsNullOrWhiteSpace(outcome)) query = query.Where(e => e.Outcome == outcome);

                var entries = await query.OrderByDescending(e => e.OccurredAt).Take(200).ToListAsync(ct);
                return TypedResults.Ok(entries.Select(ToDto).ToList());
            })
            .WithTags("Audit")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        return app;
    }

    private static Guid DefaultTenantId(IConfiguration config) =>
        Guid.TryParse(config["Tenancy:DefaultTenantId"], out var tenantId)
            ? tenantId
            : new Guid("00000000-0000-0000-0000-000000000001");

    private static AuditEntryDto ToDto(AuditProjection e) =>
        new(e.Id, e.Sequence, e.OccurredAt, e.ActorId, e.ActorRole, e.Action, e.ResourceType, e.ResourceId, e.Outcome, e.Summary);
}

public record AuditEntryDto(
    Guid Id, long Sequence, DateTimeOffset OccurredAt, Guid? ActorId, string? ActorRole,
    string Action, string ResourceType, string ResourceId, string Outcome, string? Summary);
