using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public static class StreamOutboxServiceCollectionExtensions
{
    public static IServiceCollection AddStreamOutbox<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddScoped<IStreamOutboxStore, EfStreamOutboxStore<TDbContext>>();
        services.AddScoped<StreamOutboxStager>();
        services.AddScoped<StreamOutboxRelay>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
