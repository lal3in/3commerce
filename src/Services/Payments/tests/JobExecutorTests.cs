using Microsoft.Extensions.Logging.Abstractions;
using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

namespace ThreeCommerce.Payments.Tests;

public class JobExecutorTests
{
    private sealed class FakeJobRunStore : IJobRunStore
    {
        public readonly List<JobRun> Runs = [];

        public void Add(JobRun run) => Runs.Add(run);

        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<List<JobRun>> RecentAsync(string? jobName, int take, CancellationToken ct) => Task.FromResult(Runs);
    }

    private sealed class FakeJob(string name, Func<Task> body) : IScheduledJob
    {
        public string Name => name;

        public string CronSchedule => "0 0 2 * * ?";

        public Task ExecuteAsync(CancellationToken ct) => body();
    }

    private static JobExecutor Executor(IJobRunStore store) =>
        new(store, TimeProvider.System, NullLogger<JobExecutor>.Instance);

    [Fact]
    public async Task Successful_job_is_recorded_as_succeeded()
    {
        var store = new FakeJobRunStore();
        var run = await Executor(store).ExecuteAsync(new FakeJob("nightly", () => Task.CompletedTask), default);

        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.NotNull(run.CompletedAt);
        Assert.Null(run.Error);
        Assert.Single(store.Runs);
    }

    [Fact]
    public async Task Failing_job_is_recorded_as_failed_and_does_not_rethrow()
    {
        var store = new FakeJobRunStore();
        var run = await Executor(store).ExecuteAsync(
            new FakeJob("boom", () => throw new InvalidOperationException("kaboom")), default);

        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Equal("kaboom", run.Error);
        Assert.NotNull(run.CompletedAt);
    }

    private static JobExecutor ExecutorWithRetries(IJobRunStore store, int retries) =>
        new(store, TimeProvider.System, NullLogger<JobExecutor>.Instance, null,
            Microsoft.Extensions.Options.Options.Create(new QuartzSchedulerOptions { MaxJobRetries = retries, RetryDelaySeconds = 0 }));

    [Fact]
    public async Task Transient_failure_is_retried_then_recorded_succeeded()
    {
        var store = new FakeJobRunStore();
        var attempts = 0;
        var run = await ExecutorWithRetries(store, 2).ExecuteAsync(
            new FakeJob("flaky", () =>
            {
                attempts++;
                return attempts < 3 ? throw new InvalidOperationException("transient") : Task.CompletedTask;
            }), default);

        Assert.Equal(3, attempts); // failed twice, succeeded on the third (1 + 2 retries)
        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Null(run.Error);
    }

    [Fact]
    public async Task Retries_are_bounded_then_recorded_failed()
    {
        var store = new FakeJobRunStore();
        var attempts = 0;
        var run = await ExecutorWithRetries(store, 2).ExecuteAsync(
            new FakeJob("always", () => { attempts++; throw new InvalidOperationException("persistent"); }), default);

        Assert.Equal(3, attempts); // original + 2 retries, then give up
        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Equal("persistent", run.Error);
    }
}
