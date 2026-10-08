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

    private static string GetRabbitMqConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString("RabbitMq") ?? "amqp://guest:guest@localhost:5672";
}
