using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;

/// <summary>
/// Loads <c>appsettings.Container.json</c> (host wiring for the containerized launch —
/// Postgres/RabbitMQ/service hostnames) when <c>USE_CONTAINER_CONFIG=true</c>.
///
/// Deliberately keyed off an explicit environment variable, NOT
/// <c>ASPNETCORE_ENVIRONMENT</c>, so the dev/prod environment — which drives the BL-11
/// <see cref="Auth.DevSecretGuard"/> and the Identity admin seeder — stays orthogonal to
/// container host wiring. The same file therefore serves both dev and prod launches, and
/// both Docker Compose and Kubernetes (service names match across all three).
/// </summary>
public static class ContainerConfig
{
    public static TBuilder AddContainerConfig<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        if (string.Equals(Environment.GetEnvironmentVariable("USE_CONTAINER_CONFIG"), "true", StringComparison.OrdinalIgnoreCase))
        {
            builder.Configuration.AddJsonFile("appsettings.Container.json", optional: true, reloadOnChange: false);
        }

        return builder;
    }
}
