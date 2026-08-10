using System.Collections.Specialized;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Scheduled-Job Manager control backbone (unit lane, in-memory Quartz — no containers): describe / run-now /
/// pause-resume / reschedule drive the Quartz scheduler and persist a ScheduleOverride so the change survives
/// restart. Cron is validated.
/// </summary>
public class JobControlServiceTests
{
    private const string Job = "noop";
    private const string DefaultCron = "0 0/5 * * * ?";

    private sealed class NoOpJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }

    private sealed class FakeOverrideStore : IScheduleOverrideStore
    {
        private readonly Dictionary<string, ScheduleOverride> _rows = new();
        public Task<List<ScheduleOverride>> AllAsync(CancellationToken ct) => Task.FromResult(_rows.Values.ToList());
        public Task<ScheduleOverride?> GetAsync(string jobName, CancellationToken ct) =>
            Task.FromResult(_rows.GetValueOrDefault(jobName));
        public Task UpsertAsync(string jobName, string? cron, bool paused, DateTimeOffset now, CancellationToken ct)
        {
            _rows[jobName] = new ScheduleOverride { JobName = jobName, Cron = cron, Paused = paused, UpdatedAt = now };
            return Task.CompletedTask;
        }
    }

    private static async Task<(JobControlService Control, FakeOverrideStore Store)> BuildAsync()
    {
        // A uniquely-named, in-memory scheduler per test (Quartz keeps a process-global registry keyed by
        // instance name). The SAME factory is shared with the service so it resolves this scheduler.
        var factory = new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
            ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz",
            ["quartz.threadPool.threadCount"] = "1",
        });
        var scheduler = await factory.GetScheduler();
        await scheduler.Start();
        var key = new JobKey(Job);
        await scheduler.ScheduleJob(
            JobBuilder.Create<NoOpJob>().WithIdentity(key).Build(),
            TriggerBuilder.Create().WithIdentity($"{Job}-trigger").ForJob(key).WithCronSchedule(DefaultCron).Build());

        var store = new FakeOverrideStore();
        var control = new JobControlService(
            factory, store,
            new RegisteredScheduledJobs { Jobs = [(Job, DefaultCron)] },
            new SchedulerIdentity("test"), TimeProvider.System, NullLogger<JobControlService>.Instance);
        return (control, store);
    }

    [Fact]
    public async Task Describe_lists_the_job_with_its_cron_and_next_fire()
    {
        var (control, _) = await BuildAsync();

        var jobs = await control.DescribeAllAsync(default);

        var job = Assert.Single(jobs);
        Assert.Equal("test", job.Service);
        Assert.Equal(Job, job.Name);
        Assert.Equal(DefaultCron, job.Cron);
        Assert.False(job.Paused);
        Assert.NotNull(job.NextFireUtc);
    }

    [Fact]
    public async Task Pause_then_resume_toggles_state_and_persists_the_override()
    {
        var (control, store) = await BuildAsync();

        Assert.True(await control.PauseAsync(Job, default));
        Assert.True((await control.DescribeAllAsync(default)).Single().Paused);
        Assert.True((await store.GetAsync(Job, default))!.Paused);

        Assert.True(await control.ResumeAsync(Job, default));
        Assert.False((await control.DescribeAllAsync(default)).Single().Paused);
        Assert.False((await store.GetAsync(Job, default))!.Paused);
    }

    [Fact]
    public async Task Reschedule_updates_the_cron_and_persists_it()
    {
        var (control, store) = await BuildAsync();

        var error = await control.RescheduleAsync(Job, "0 0 3 * * ?", default);

        Assert.Null(error);
        Assert.Equal("0 0 3 * * ?", (await control.DescribeAllAsync(default)).Single().Cron);
        Assert.Equal("0 0 3 * * ?", (await store.GetAsync(Job, default))!.Cron);
    }

    [Fact]
    public async Task Reschedule_rejects_an_invalid_cron_without_changing_anything()
    {
        var (control, store) = await BuildAsync();

        var error = await control.RescheduleAsync(Job, "not-a-cron", default);

        Assert.Equal("Invalid cron expression.", error);
        Assert.Null(await store.GetAsync(Job, default)); // nothing persisted
    }

    [Fact]
    public async Task Run_now_returns_false_for_an_unknown_job()
    {
        var (control, _) = await BuildAsync();

        Assert.True(await control.RunNowAsync(Job, default));
        Assert.False(await control.RunNowAsync("ghost", default));
    }
}
