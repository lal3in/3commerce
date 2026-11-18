using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ThreeCommerce.BuildingBlocks.Observability.Blazor;

public static class BlazorTelemetryExtensions
{
    /// <summary>
    /// Telemetry for a Blazor Server app (Admin, SupplierPortal — audit P1-4): <see cref="TelemetryExtensions.AddTelemetryCore"/>
    /// (traces/metrics/logs + ADR-0064 release identity, no MassTransit/EF) plus the .NET 10 Blazor component and
    /// circuit instruments, and the <see cref="TelemetryCircuitHandler"/>. The app's outgoing gateway calls carry
    /// <c>traceparent</c> (HttpClient instrumentation), so an operator action is ONE trace from the Blazor server
    /// through the gateway into the services.
    /// </summary>
    public static ReleaseIdentity AddBlazorServerTelemetry<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        var release = builder.AddTelemetryCore(
            serviceName,
            tracing => tracing.AddSource(TelemetryNames.ComponentsActivitySource),
            metrics => metrics
                .AddMeter(TelemetryNames.ComponentsMeter)
                .AddMeter(TelemetryNames.ComponentsLifecycleMeter)
                .AddMeter(TelemetryNames.CircuitsMeter));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, TelemetryCircuitHandler>());
        return release;
    }
}
