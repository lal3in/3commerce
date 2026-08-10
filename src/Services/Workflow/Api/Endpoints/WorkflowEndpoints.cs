using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Workflow.Domain;
using ThreeCommerce.Workflow.Infrastructure;

namespace ThreeCommerce.Workflow.Api.Endpoints;

/// <summary>Central scheduled-job run history (mt6_3), read-only.</summary>
public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowRuns(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/workflow/runs", async (string? job, WorkflowDbContext db, CancellationToken ct) =>
            {
                var query = db.Runs.AsNoTracking();
                if (!string.IsNullOrWhiteSpace(job)) query = query.Where(r => r.JobName == job);
                var runs = await query.OrderByDescending(r => r.StartedAt).Take(200).ToListAsync(ct);
                return TypedResults.Ok(runs.Select(ToDto).ToList());
            })
            .WithTags("Workflow")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        return app;
    }

    /// <summary>The cross-service Scheduled-Job Manager view: every registered job (schedule + state) merged
    /// with its latest run. Feeds Mission Control's job manager; control actions route to the owning service.</summary>
    public static IEndpointRouteBuilder MapWorkflowJobs(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/workflow/jobs", async (WorkflowDbContext db, CancellationToken ct) =>
            {
                var jobs = await db.ScheduledJobs.AsNoTracking().OrderBy(j => j.Service).ThenBy(j => j.Name).ToListAsync(ct);
                var names = jobs.Select(j => j.Name).ToList();
                // Latest run per job name (job names are unique across services in practice).
                var latest = await db.Runs.AsNoTracking()
                    .Where(r => names.Contains(r.JobName))
                    .GroupBy(r => r.JobName)
                    .Select(g => g.OrderByDescending(r => r.StartedAt).First())
                    .ToListAsync(ct);
                var byName = latest.ToDictionary(r => r.JobName);

                var dtos = jobs.Select(j =>
                {
                    var run = byName.GetValueOrDefault(j.Name);
                    var durationMs = run?.CompletedAt is { } done ? (long)(done - run.StartedAt).TotalMilliseconds : (long?)null;
                    return new ScheduledJobViewDto(
                        j.Service, j.Name, j.Cron, j.Paused, j.NextFireUtc,
                        run?.Status, run?.StartedAt, durationMs, run?.Error);
                }).ToList();
                return TypedResults.Ok(dtos);
            })
            .WithTags("Workflow")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        return app;
    }

    private static WorkflowRunDto ToDto(WorkflowRun r) => new(
        r.Id, r.JobName, r.Status, r.StartedAt, r.CompletedAt,
        r.CompletedAt is { } done ? (long)(done - r.StartedAt).TotalMilliseconds : null, r.Error);
}

public record WorkflowRunDto(
    Guid Id, string JobName, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, long? DurationMs, string? Error);

public record ScheduledJobViewDto(
    string Service, string Name, string Cron, bool Paused, DateTimeOffset? NextFireUtc,
    string? LastStatus, DateTimeOffset? LastStartedAt, long? LastDurationMs, string? LastError);
