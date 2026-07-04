using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Marketing.Domain;
using ThreeCommerce.Marketing.Infrastructure;

namespace ThreeCommerce.Marketing.Api.Endpoints;

/// <summary>
/// Analytics event collection (def_4 / mt5_5). POST /events is anonymous — shoppers have no
/// session — and consent is enforced at the SOURCE (the storefront batcher is a no-op without
/// Analytics consent); the server's job is sanitization, dedupe, and coarse-IP storage (mt5_4).
/// </summary>
public static class AnalyticsEndpoints
{
    /// <summary>Matches the storefront batcher's MAX_BATCH with headroom; bigger requests are rejected, not truncated.</summary>
    public const int MaxBatch = 50;

    public static IEndpointRouteBuilder MapAnalytics(this IEndpointRouteBuilder app)
    {
        app.MapPost("/events", Collect).WithTags("Analytics")
            .WithSummary("Accept a batch of storefront analytics events (consent-gated at the client).");
        app.MapGet("/admin/analytics/events", List).WithTags("Analytics")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy)
            .WithSummary("Most recent analytics events for a tenant.");
        return app;
    }

    private static async Task<Results<Accepted<CollectResponse>, BadRequest<string>>> Collect(
        AnalyticsBatchRequest request, HttpContext http, MarketingDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (request.Events is null or { Count: 0 })
        {
            return TypedResults.BadRequest("The batch is empty.");
        }

        if (request.Events.Count > MaxBatch)
        {
            return TypedResults.BadRequest($"Batch too large; send at most {MaxBatch} events.");
        }

        var tenantId = HeaderGuid(http, "X-3C-Tenant-Id") ?? Guid.Parse("00000000-0000-0000-0000-000000000001");
        var batchIds = request.Events.Where(e => e.EventId is not null).Select(e => e.EventId!).ToList();
        var alreadyStored = (await db.AnalyticsEvents
            .Where(e => e.TenantId == tenantId && batchIds.Contains(e.EventId))
            .Select(e => e.EventId)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var result = AnalyticsCollector.Accept(tenantId, request.Events, alreadyStored);
        var now = time.GetUtcNow();
        var coarseIp = IpAnonymizer.Anonymize(http.Connection.RemoteIpAddress?.ToString());
        foreach (var accepted in result.Accepted)
        {
            db.AnalyticsEvents.Add(new AnalyticsEventRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = accepted.TenantId,
                SchemaVersion = accepted.SchemaVersion,
                EventType = accepted.EventType,
                VisitorId = accepted.VisitorId,
                SessionId = accepted.SessionId,
                CustomerId = accepted.CustomerId,
                AnalyticsConsent = accepted.AnalyticsConsent,
                OccurredAt = accepted.OccurredAt,
                EventId = accepted.EventId,
                PayloadJson = JsonSerializer.Serialize(accepted.Payload),
                ClientIpCoarse = coarseIp,
                ReceivedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted((string?)null, new CollectResponse(result.Accepted.Count, result.Rejected));
    }

    private static async Task<Ok<List<AnalyticsEventDto>>> List(
        Guid tenantId, MarketingDbContext db, int? take, CancellationToken ct)
    {
        var events = await db.AnalyticsEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.ReceivedAt)
            .Take(Math.Clamp(take ?? 100, 1, 500))
            .Select(e => new AnalyticsEventDto(
                e.Id, e.EventType, e.VisitorId, e.SessionId, e.OccurredAt, e.EventId, e.PayloadJson, e.ClientIpCoarse, e.ReceivedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(events);
    }

    private static Guid? HeaderGuid(HttpContext http, string name) =>
        Guid.TryParse(http.Request.Headers[name].FirstOrDefault(), out var id) ? id : null;
}

public record AnalyticsBatchRequest(List<AnalyticsEventInput>? Events);

public record CollectResponse(int Accepted, IReadOnlyList<string> Rejected);

public record AnalyticsEventDto(
    Guid Id, string EventType, string? VisitorId, string? SessionId, DateTimeOffset OccurredAt,
    string EventId, string PayloadJson, string ClientIpCoarse, DateTimeOffset ReceivedAt);
