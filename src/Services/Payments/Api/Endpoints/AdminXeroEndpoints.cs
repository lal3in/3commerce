using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain.Xero;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Xero;

namespace ThreeCommerce.Payments.Api.Endpoints;

public static class AdminXeroEndpoints
{
    public static IEndpointRouteBuilder MapAdminXero(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/xero").WithTags("Xero").RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        group.MapGet("/sync-runs", ListSyncRuns);
        group.MapPost("/sync/{date}", RunDaily); // operator-triggered (also the cron target)
        return app;
    }

    private static async Task<Ok<List<SyncRunDto>>> ListSyncRuns(PaymentsDbContext db, CancellationToken ct)
    {
        var runs = await db.Set<SyncRun>().AsNoTracking()
            .OrderByDescending(s => s.CreatedAt).Take(100)
            .Select(s => new SyncRunDto(s.Reference, s.Status.ToString(), s.XeroJournalId, s.Error, s.CreatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(runs);
    }

    private static async Task<Results<Ok<string>, BadRequest<string>>> RunDaily(
        string date, DailyJournalJob job, CancellationToken ct)
    {
        if (!DateOnly.TryParse(date, out var parsed))
        {
            return TypedResults.BadRequest("Date must be yyyy-MM-dd.");
        }

        var status = await job.RunForAsync(parsed, ct);
        return TypedResults.Ok(status.ToString());
    }
}

public record SyncRunDto(string Reference, string Status, string? XeroJournalId, string? Error, DateTimeOffset CreatedAt);
