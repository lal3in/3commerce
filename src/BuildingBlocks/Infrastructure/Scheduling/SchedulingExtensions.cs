using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

/// <summary>Registers typed scheduled jobs (mt6_3).</summary>
public sealed class ScheduledJobs(IServiceCollection services)
{
    internal readonly List<(string Name, string Cron)> Registered = [];

    public ScheduledJobs Add<TJob>(string name, string cron)
        where TJob : class, IScheduledJob
    {
        services.AddScoped<IScheduledJob, TJob>();
        Registered.Add((name, cron));
        return this;
    }
}

public static class SchedulingExtensions
{
    /// <summary>Map the local job-run table the same way in every service (mt6_3). Uses the default schema.</summary>
    public static ModelBuilder ConfigureJobRuns(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobRun>(run =>
        {
            run.ToTable("JobRuns");
            run.HasKey(x => x.Id);
            run.Property(x => x.JobName).HasMaxLength(128);
            run.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            run.Property(x => x.Error).HasMaxLength(1024);
            run.HasIndex(x => new { x.JobName, x.StartedAt });
        });

        return modelBuilder;
    }

    /// <summary>
    /// Wire Quartz-driven recurring jobs (mt6_3): each registered <see cref="IScheduledJob"/> gets a cron
    /// trigger, and every fire is recorded as a <see cref="JobRun"/> by the executor. Quartz resolves the
    /// job in a per-execution DI scope, so jobs may use scoped services (DbContext etc.).
    /// </summary>
    public static IServiceCollection AddScheduledJobs(this IServiceCollection services, Action<ScheduledJobs> configure)
    {
        var registrar = new ScheduledJobs(services);
        configure(registrar);
        services.AddScoped<JobExecutor>();

        services.AddQuartz(quartz =>
        {
            foreach (var (name, cron) in registrar.Registered)
            {
                var key = new JobKey(name);
                quartz.AddJob<QuartzScheduledJobAdapter>(key, job => job.StoreDurably());
                quartz.AddTrigger(trigger => trigger.ForJob(key).WithIdentity($"{name}-trigger").WithCronSchedule(cron));
            }
        });
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        return services;
    }
}

/// <summary>Quartz → IScheduledJob bridge (mt6_3): resolve the job by key name and run it via the executor.</summary>
[DisallowConcurrentExecution]
internal sealed class QuartzScheduledJobAdapter(IEnumerable<IScheduledJob> jobs, JobExecutor executor) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var job = jobs.FirstOrDefault(j => j.Name == context.JobDetail.Key.Name);
        if (job is not null)
        {
            await executor.ExecuteAsync(job, context.CancellationToken);
        }
    }
}
