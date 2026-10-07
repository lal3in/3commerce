using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Bus lifecycle for every service host a fixture builds (WebApplicationFactory). Each fixture's
/// <c>CreateFactory</c> applies it, so tests never configure it themselves.
/// </summary>
internal static class TestBusHosting
{
    /// <summary>
    /// Makes the host's start block until its MassTransit bus is started: every receive endpoint's queue
    /// declared, bound and consuming. Without it, <c>MassTransitHostedService.StartAsync</c> returns at once
    /// and starts the bus in the background, which goes wrong in two ways:
    /// <list type="number">
    /// <item>A host disposed before that start finishes (short tests, or create-then-dispose to declare a
    /// queue) is never stopped. <c>MassTransitBus.StopAsync</c> only logs "Failed to stop bus … (Not Started)"
    /// while the start is in flight, and the start then completes. The result is a zombie bus whose
    /// <c>IServiceProvider</c> is already disposed. It keeps competing for its service's durable queues on the
    /// shared broker, and every message it takes fails with ObjectDisposedException and ends up in
    /// <c>_error</c>. A later test then waits for a projection that never happens (the
    /// StorefrontGoLiveReadinessTests flake).</item>
    /// <item>A message published before a new queue is bound is dropped by RabbitMQ.</item>
    /// </list>
    /// The start is bounded, so a broker that never comes up fails the factory loudly instead of hanging.
    /// </summary>
    public static IServiceCollection WaitForBusStartup(this IServiceCollection services) =>
        services.Configure<MassTransitHostOptions>(options =>
        {
            options.WaitUntilStarted = true;
            options.StartTimeout = TimeSpan.FromSeconds(60);
        });
}
