using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

    /// <summary>Map the per-service schedule-override table (Scheduled-Job Manager). Uses the default schema.</summary>
    public static ModelBuilder ConfigureScheduleOverrides(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScheduleOverride>(o =>
        {
            o.ToTable("ScheduleOverrides");
            o.HasKey(x => x.JobName);
            o.Property(x => x.JobName).HasMaxLength(128);
            o.Property(x => x.Cron).HasMaxLength(128);
        });

        return modelBuilder;
    }

    /// <summary>
    /// Wire Quartz-driven recurring jobs (mt6_3): each registered <see cref="IScheduledJob"/> gets a cron
    /// trigger, and every fire is recorded as a <see cref="JobRun"/> by the executor. Quartz resolves the
    /// job in a per-execution DI scope, so jobs may use scoped services (DbContext etc.). <paramref name="serviceName"/>
    /// is the owning service key stamped on run/descriptor events so the manager can route control here.
    /// </summary>
    public static IServiceCollection AddScheduledJobs(this IServiceCollection services, string serviceName, Action<ScheduledJobs> configure) =>
        services.AddScheduledJobs(null, serviceName, configure);

    public static IServiceCollection AddScheduledJobs(this IServiceCollection services, IConfiguration? configuration, string serviceName, Action<ScheduledJobs> configure)
    {
        var registrar = new ScheduledJobs(services);
        configure(registrar);
        services.AddScoped<JobExecutor>();
        services.AddSingleton(new SchedulerIdentity(serviceName));
        services.AddSingleton(new RegisteredScheduledJobs { Jobs = registrar.Registered.Select(r => (r.Name, r.Cron)).ToList() });
        services.AddScoped<JobControlService>();
        // Applies persisted overrides + publishes the initial descriptors once the app (and Quartz) start.
        services.AddHostedService<ScheduledJobRegistrar>();

        var options = configuration?.GetSection(QuartzSchedulerOptions.SectionName).Get<QuartzSchedulerOptions>() ?? new QuartzSchedulerOptions();
        services.Configure<QuartzSchedulerOptions>(configuration?.GetSection(QuartzSchedulerOptions.SectionName) ?? new ConfigurationBuilder().Build().GetSection(QuartzSchedulerOptions.SectionName));

        services.AddQuartz(quartz =>
        {
            quartz.SchedulerName = options.SchedulerName;
            quartz.SchedulerId = options.InstanceId;
            quartz.MisfireThreshold = TimeSpan.FromSeconds(options.MisfireThresholdSeconds);

            if (options.PersistentStoreEnabled)
            {
                if (string.IsNullOrWhiteSpace(options.ConnectionString))
                    throw new InvalidOperationException("Quartz persistent store requires Quartz:ConnectionString.");

                quartz.UsePersistentStore(store =>
                {
                    store.UseProperties = true;
                    store.UsePostgres(postgres =>
                    {
                        postgres.ConnectionString = options.ConnectionString;
                        postgres.TablePrefix = options.TablePrefix;
                    });
                    store.UseClustering(cluster =>
                    {
                        cluster.CheckinInterval = TimeSpan.FromSeconds(options.ClusterCheckinIntervalSeconds);
                        cluster.CheckinMisfireThreshold = TimeSpan.FromSeconds(options.ClusterCheckinMisfireThresholdSeconds);
                    });
                });
            }

            foreach (var (name, cron) in registrar.Registered)
            {
                var key = new JobKey(name);
                quartz.AddJob<QuartzScheduledJobAdapter>(key, job => job.StoreDurably());
                quartz.AddTrigger(trigger => trigger
                    .ForJob(key)
                    .WithIdentity($"{name}-trigger")
                    .WithCronSchedule(cron, cronOptions => ApplyMisfirePolicy(cronOptions, options.MisfirePolicy)));
            }
        });
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        return services;
    }

    // Map the configured misfire policy onto the cron trigger. Unknown values fall back to DoNothing so a
    // typo can never leave a trigger with no misfire handling.
    private static void ApplyMisfirePolicy(CronScheduleBuilder builder, string policy)
    {
        switch (policy?.Trim().ToLowerInvariant())
        {
            case "fireandproceed":
                builder.WithMisfireHandlingInstructionFireAndProceed();
                break;
            case "ignore":
                builder.WithMisfireHandlingInstructionIgnoreMisfires();
                break;
            default:
                builder.WithMisfireHandlingInstructionDoNothing();
                break;
        }
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
