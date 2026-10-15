using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;

public static class MassTransitExtensions
{
    /// <summary>
    /// The one endpoint-name formatter every bus uses (ADR-0060). Kebab-case, no prefix: a receive endpoint (queue)
    /// is named from the consumer / saga type name ALONE, and every service shares the broker vhost — so two
    /// services with a same-named consumer bind one queue and COMPETE for its messages instead of each getting a
    /// copy. Consumer class names must therefore be unique across services (prefix a duplicate with its service,
    /// e.g. <c>FulfillmentOrderConfirmedConsumer</c>); <c>ConsumerEndpointNameTests</c> enforces it.
    /// </summary>
    public static IEndpointNameFormatter EndpointNameFormatter { get; } = KebabCaseEndpointNameFormatter.Instance;

    /// <summary>
    /// Setting that bounds how long a host waits for its bus to start (env <c>Messaging__StartTimeoutSeconds</c>).
    /// </summary>
    public const string StartTimeoutSetting = "Messaging:StartTimeoutSeconds";

    /// <summary>
    /// Default bus start bound. Long enough to ride out a RabbitMQ that is still booting (a fresh Kubernetes pod, a
    /// cold container), short enough that a broker that is really gone fails the host instead of hanging it.
    /// </summary>
    public const int DefaultStartTimeoutSeconds = 120;

    /// <summary>
    /// The EF outbox's <c>QueryMessageLimit</c>. MassTransit's bus-outbox delivery service runs its passes through a
    /// <c>RequestRateAlgorithm</c> whose parallelism is <c>ceil(QueryMessageLimit / 10)</c>: at the default 100 it
    /// runs up to SIX delivery transactions at once after any pass of 10+ messages. Each takes the oldest unlocked
    /// batch (<c>… ORDER BY "Created" LIMIT 1 FOR UPDATE SKIP LOCKED</c>) inside a REPEATABLE READ transaction, so one
    /// that meets a batch another just updated fails with <c>40001 could not serialize access due to concurrent
    /// update</c>. The service's retry then backs off exponentially (3 s, then up to 30 s per attempt) and EVERY
    /// pending message of the service waits — a projection 20–30 s late — and batches delivered in parallel reach
    /// consumers out of publish order. At 10 the request limit is 1: one delivery transaction per host, batches in
    /// <c>Created</c> order, no self-inflicted 40001. Each pass still delivers whole batches (MessageDeliveryLimit
    /// 100), and the inbox cleanup it also sizes loops until nothing is left to delete. Replicas of a service still
    /// deliver in parallel, so events whose order matters carry their own version (ADR-0043 amendment 2026-10-16).
    /// </summary>
    public const int OutboxDeliveryQueryLimit = 10;

    /// <summary>
    /// Service bus for services that own a database: EF transactional outbox (writes and
    /// publishes commit atomically) plus inbox-based consumer idempotency on every endpoint.
    /// </summary>
    public static IServiceCollection AddServiceBus<TDbContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configure = null,
        Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureTransport = null)
        where TDbContext : DbContext
    {
        services.WaitForBusStartup(configuration);
        services.AddMassTransit(bus =>
        {
            bus.SetEndpointNameFormatter(EndpointNameFormatter);

            bus.AddEntityFrameworkOutbox<TDbContext>(outbox =>
            {
                outbox.UsePostgres();
                // Resolve the outbox/inbox table schema from the DbContext model on every call,
                // not a static cache keyed by the shared OutboxState type. With named schemas
                // (HasDefaultSchema), the default cache leaks one service's schema to others when
                // multiple services share a process (integration tests / in-process hosts).
                outbox.LockStatementProvider = new PostgresLockStatementProvider(enableSchemaCaching: false);
                // One outbox delivery transaction at a time per host (see OutboxDeliveryQueryLimit).
                outbox.QueryMessageLimit = OutboxDeliveryQueryLimit;
                outbox.UseBusOutbox();
            });

            bus.AddConfigureEndpointsCallback((context, _, cfg) =>
            {
                cfg.UseEntityFrameworkOutbox<TDbContext>(context);
                cfg.UseMessageRetry(retry => retry.Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
            });

            configure?.Invoke(bus);

            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(GetRabbitMqConnectionString(configuration)));
                configureTransport?.Invoke(context, cfg);
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }

    /// <summary>
    /// Service bus without persistence, for workers with no database.
    /// Consumers registered here must tolerate redelivery (no inbox dedup).
    /// </summary>
    public static IServiceCollection AddServiceBus(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configure = null)
    {
        services.WaitForBusStartup(configuration);
        services.AddMassTransit(bus =>
        {
            bus.SetEndpointNameFormatter(EndpointNameFormatter);

            configure?.Invoke(bus);

            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(GetRabbitMqConnectionString(configuration)));
                cfg.UseMessageRetry(retry => retry.Incremental(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }

    /// <summary>
    /// Makes the host's start wait until the bus is started: every receive endpoint's queue declared, bound to its
    /// message exchanges and consuming. MassTransit's hosted service otherwise returns from <c>StartAsync</c> at once
    /// and starts the bus in the background, so a service would listen for HTTP, report ready and publish (outbox)
    /// before its own queues exist, and on a fresh broker a message published to an exchange with no queue bound yet
    /// is dropped. Kestrel starts after every other hosted service (WebApplicationBuilder registers it last), so a
    /// service now accepts no request until its bus is up. A stop requested mid-start (SIGTERM during boot) cancels
    /// the start and stops the bus, instead of the bus finishing its start in the background as a zombie consumer.
    /// <para>
    /// Failure behaviour: the start is bounded by <see cref="StartTimeoutSetting"/> (default
    /// <see cref="DefaultStartTimeoutSeconds"/>). Until then MassTransit keeps retrying the broker connection; when
    /// the bound elapses MassTransit stops the half-started bus and the host's start throws, so the process exits
    /// non-zero with "Bus start canceled" in its log. Compose (<c>restart: unless-stopped</c>) and Kubernetes
    /// restart it; bare-run (<c>dev-up.sh</c>) starts no service before RabbitMQ is healthy. No <c>StopTimeout</c>
    /// is set: the host's shutdown token already bounds the stop.
    /// </para>
    /// </summary>
    private static void WaitForBusStartup(this IServiceCollection services, IConfiguration configuration) =>
        services.AddOptions<MassTransitHostOptions>().Configure(options =>
        {
            options.WaitUntilStarted = true;
            options.StartTimeout = TimeSpan.FromSeconds(GetStartTimeoutSeconds(configuration));
        });

    private static int GetStartTimeoutSeconds(IConfiguration configuration)
    {
        var seconds = configuration.GetValue(StartTimeoutSetting, DefaultStartTimeoutSeconds);
        return seconds > 0
            ? seconds
            : throw new InvalidOperationException($"{StartTimeoutSetting} must be a positive number of seconds, got {seconds}.");
    }

    private static string GetRabbitMqConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString("RabbitMq") ?? "amqp://guest:guest@localhost:5672";
}
