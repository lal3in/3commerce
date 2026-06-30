using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public static class StreamProducerServiceCollectionExtensions
{
    public static IServiceCollection AddStreamProducer(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StreamProducerOptions>(configuration.GetSection(StreamProducerOptions.SectionName));
        var options = configuration.GetSection(StreamProducerOptions.SectionName).Get<StreamProducerOptions>() ?? new StreamProducerOptions();

        if (options.Enabled)
        {
            services.AddSingleton<IStreamEventProducer, KafkaStreamEventProducer>();
        }
        else
        {
            services.AddSingleton<FakeStreamEventProducer>();
            services.AddSingleton<IStreamEventProducer>(sp => sp.GetRequiredService<FakeStreamEventProducer>());
        }

        return services;
    }
}
