using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public static class StreamConsumerServiceCollectionExtensions
{
    public static IServiceCollection AddStreamConsumer<TPayload, THandler>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where THandler : class, IStreamEventHandler<TPayload>
    {
        services.Configure<StreamConsumerOptions>(configuration.GetSection(sectionName));
        services.AddScoped<IStreamEventHandler<TPayload>, THandler>();
        services.AddScoped<StreamEventConsumerProcessor<TPayload>>();
        services.AddSingleton<IStreamProcessedEventStore, InMemoryStreamProcessedEventStore>();
        services.AddSingleton<IStreamDeadLetterSink, InMemoryStreamDeadLetterSink>();
        services.AddHostedService<KafkaStreamConsumerService<TPayload>>();
        return services;
    }
}
