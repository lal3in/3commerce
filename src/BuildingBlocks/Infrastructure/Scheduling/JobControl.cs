using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

/// <summary>
/// Runtime control of this host's scheduled jobs over the Quartz <see cref="ISchedulerFactory"/> (the
/// Scheduled-Job Manager): describe, run-now, pause/resume, reschedule. Every mutation persists a
/// <see cref="ScheduleOverride"/> (so it survives restart — re-applied by <see cref="ScheduledJobRegistrar"/>)
/// and re-publishes a <see cref="ScheduledJobDescriptor"/> so the central Workflow registry stays live.
/// </summary>
public sealed class JobControlService(
    ISchedulerFactory schedulerFactory,
    IScheduleOverrideStore overrides,
    RegisteredScheduledJobs registered,
    SchedulerIdentity identity,
    TimeProvider clock,
    ILogger<JobControlService> logger,
    IPublishEndpoint? publisher = null)
{
    private static JobKey JobKeyFor(string name) => new(name);
    private static TriggerKey TriggerKeyFor(string name) => new($"{name}-trigger");

    private bool Known(string name) => registered.Jobs.Any(j => j.Name == name);

    public async Task<List<ScheduledJobDescriptor>> DescribeAllAsync(CancellationToken ct)
    {
        if (registered.Jobs.Count == 0)
        {
            return [];
        }

        var scheduler = await schedulerFactory.GetScheduler(ct);
        var stored = (await overrides.AllAsync(ct)).ToDictionary(o => o.JobName);
        var list = new List<ScheduledJobDescriptor>();
        foreach (var (name, defaultCron) in registered.Jobs)
        {
            list.Add(await DescribeAsync(scheduler, name, defaultCron, stored.GetValueOrDefault(name), ct));
        }

        return list;
    }

    /// <summary>Startup: re-apply persisted overrides (cron + paused) to the Quartz triggers, then publish
    /// each job's current descriptor so the Workflow registry is populated on boot.</summary>
    public async Task ApplyOverridesAndPublishAsync(CancellationToken ct)
    {
        if (registered.Jobs.Count == 0)
        {
            return; // a service with no jobs (e.g. the Workflow aggregator) needs no override table
        }

        var scheduler = await schedulerFactory.GetScheduler(ct);
        var stored = (await overrides.AllAsync(ct)).ToDictionary(o => o.JobName);
        foreach (var (name, defaultCron) in registered.Jobs)
        {
            var ov = stored.GetValueOrDefault(name);
            if (ov?.Cron is { Length: > 0 } cron && CronExpression.IsValidExpression(cron))
            {
                await RescheduleTriggerAsync(scheduler, name, cron, ct);
            }

            if (ov?.Paused == true)
            {
                await scheduler.PauseJob(JobKeyFor(name), ct);
            }

            await PublishAsync(scheduler, name, defaultCron, ov, ct);
        }
    }

    public async Task<bool> RunNowAsync(string name, CancellationToken ct)
    {
        if (!Known(name))
        {
            return false;
        }

        var scheduler = await schedulerFactory.GetScheduler(ct);
        await scheduler.TriggerJob(JobKeyFor(name), ct);
        logger.LogInformation("Scheduled job {Job} triggered on demand", name);
        return true;
    }

    public Task<bool> PauseAsync(string name, CancellationToken ct) => SetPausedAsync(name, paused: true, ct);

    public Task<bool> ResumeAsync(string name, CancellationToken ct) => SetPausedAsync(name, paused: false, ct);

    private async Task<bool> SetPausedAsync(string name, bool paused, CancellationToken ct)
    {
        if (!Known(name))
        {
            return false;
        }

        var scheduler = await schedulerFactory.GetScheduler(ct);
        if (paused)
        {
            await scheduler.PauseJob(JobKeyFor(name), ct);
        }
        else
        {
            await scheduler.ResumeJob(JobKeyFor(name), ct);
        }

        var ov = await overrides.GetAsync(name, ct);
        await overrides.UpsertAsync(name, ov?.Cron, paused, clock.GetUtcNow(), ct);
        await PublishAsync(scheduler, name, DefaultCron(name), await overrides.GetAsync(name, ct), ct);
        logger.LogInformation("Scheduled job {Job} {State}", name, paused ? "paused" : "resumed");
        return true;
    }

    /// <summary>Reschedule to a new cron. Returns null on success, or a validation message.</summary>
    public async Task<string?> RescheduleAsync(string name, string cron, CancellationToken ct)
    {
        if (!Known(name))
        {
            return "Unknown job.";
        }

        if (string.IsNullOrWhiteSpace(cron) || !CronExpression.IsValidExpression(cron))
        {
            return "Invalid cron expression.";
        }

        var scheduler = await schedulerFactory.GetScheduler(ct);
        await RescheduleTriggerAsync(scheduler, name, cron, ct);
        var ov = await overrides.GetAsync(name, ct);
        await overrides.UpsertAsync(name, cron, ov?.Paused ?? false, clock.GetUtcNow(), ct);
        await PublishAsync(scheduler, name, DefaultCron(name), await overrides.GetAsync(name, ct), ct);
        logger.LogInformation("Scheduled job {Job} rescheduled to {Cron}", name, cron);
        return null;
    }

    private static async Task RescheduleTriggerAsync(IScheduler scheduler, string name, string cron, CancellationToken ct)
    {
        var trigger = TriggerBuilder.Create()
            .WithIdentity(TriggerKeyFor(name))
            .ForJob(JobKeyFor(name))
            .WithCronSchedule(cron)
            .Build();
        await scheduler.RescheduleJob(TriggerKeyFor(name), trigger, ct);
    }

    private async Task<ScheduledJobDescriptor> DescribeAsync(IScheduler scheduler, string name, string defaultCron, ScheduleOverride? ov, CancellationToken ct)
    {
        var cron = ov?.Cron is { Length: > 0 } c ? c : defaultCron;
        var state = await scheduler.GetTriggerState(TriggerKeyFor(name), ct);
        var paused = state == TriggerState.Paused || (ov?.Paused ?? false);
        var next = (await scheduler.GetTrigger(TriggerKeyFor(name), ct))?.GetNextFireTimeUtc();
        return new ScheduledJobDescriptor(identity.ServiceName, name, cron, paused, next);
    }

    private async Task PublishAsync(IScheduler scheduler, string name, string defaultCron, ScheduleOverride? ov, CancellationToken ct)
    {
        if (publisher is null)
        {
            return;
        }

        await publisher.Publish(await DescribeAsync(scheduler, name, defaultCron, ov, ct), ct);
    }

    private string DefaultCron(string name) => registered.Jobs.First(j => j.Name == name).DefaultCron;
}

/// <summary>Applies persisted schedule overrides and publishes the initial job descriptors once the host (and
/// the Quartz hosted service) have started. Runs after Quartz because it is registered after it.</summary>
public sealed class ScheduledJobRegistrar(IServiceProvider services, ILogger<ScheduledJobRegistrar> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = services.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<JobControlService>();
            await control.ApplyOverridesAndPublishAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Never block startup on registry priming — the jobs still run on their code-default schedule.
            logger.LogWarning(ex, "Scheduled-job registry priming failed; jobs run on their default schedules");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class JobControlEndpoints
{
    /// <summary>Admin control surface for THIS host's scheduled jobs (Scheduled-Job Manager). The gateway
    /// forwards <c>/api/{service}/admin/jobs/*</c> so Mission Control can drive every service's jobs.</summary>
    public static IEndpointRouteBuilder MapJobControl(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/jobs").WithTags("Jobs").RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", async (JobControlService control, CancellationToken ct) =>
            TypedResults.Ok(await control.DescribeAllAsync(ct)));

        group.MapPost("/{name}/run", async Task<Results<Ok, NotFound>> (string name, JobControlService control, CancellationToken ct) =>
            await control.RunNowAsync(name, ct) ? TypedResults.Ok() : TypedResults.NotFound());

        group.MapPost("/{name}/pause", async Task<Results<Ok, NotFound>> (string name, JobControlService control, CancellationToken ct) =>
            await control.PauseAsync(name, ct) ? TypedResults.Ok() : TypedResults.NotFound());

        group.MapPost("/{name}/resume", async Task<Results<Ok, NotFound>> (string name, JobControlService control, CancellationToken ct) =>
            await control.ResumeAsync(name, ct) ? TypedResults.Ok() : TypedResults.NotFound());

        group.MapPut("/{name}/schedule", async Task<Results<Ok, NotFound, BadRequest<string>>> (
            string name, RescheduleRequest request, JobControlService control, CancellationToken ct) =>
        {
            var error = await control.RescheduleAsync(name, request.Cron, ct);
            return error switch
            {
                null => TypedResults.Ok(),
                "Unknown job." => TypedResults.NotFound(),
                _ => TypedResults.BadRequest(error),
            };
        });

        return app;
    }
}

public record RescheduleRequest(string Cron);
