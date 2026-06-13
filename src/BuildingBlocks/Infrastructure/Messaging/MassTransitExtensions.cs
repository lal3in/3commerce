using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;

public static class MassTransitExtensions
{
    /// <summary>
    /// Service bus for services that own a database: EF transactional outbox (writes and
    /// publishes commit atomically) plus inbox-based consumer idempotency on every endpoint.
    /// </summary>
    public static IServiceCollection AddServiceBus<TDbContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configure = null)
        where TDbContext : DbContext
    {
        services.AddMassTransit(bus =>
        {
            bus.SetKebabCaseEndpointNameFormatter();

            bus.AddEntityFrameworkOutbox<TDbContext>(outbox =>
            {
                outbox.UsePostgres();
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
            bus.SetKebabCaseEndpointNameFormatter();

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
