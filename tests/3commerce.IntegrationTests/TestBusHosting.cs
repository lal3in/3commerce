using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

    /// <summary>
    /// <see cref="WaitForBusStartup"/>, <see cref="BusStopsBeforeHostDisposal"/>, and a bus observer that
    /// reports the host's bus starts and stops to the fixture's <see cref="TestHostTracker"/>, so an undisposed
    /// host fails the fixture's teardown.
    /// </summary>
    public static IServiceCollection AddTestBusHosting(this IServiceCollection services, TrackedHost host)
    {
        services.WaitForBusStartup().AddBusObserver(_ => host);
        // Registered after the app's services (WithWebHostBuilder's ConfigureServices runs last), so the host
        // stops it before MassTransit's hosted service.
        services.AddHostedService<BusStopsBeforeHostDisposal>();
        return services;
    }
}

/// <summary>
/// Makes every caller of the host's <c>StopAsync</c> wait until the bus has actually stopped.
/// <para>
/// A minimal-hosting app under <c>WebApplicationFactory</c> gets <c>Host.StopAsync</c> twice, concurrently:
/// once from the factory's dispose, and once from the app's own <c>app.Run()</c>, which wakes on
/// ApplicationStopping. <c>Host.StopAsync</c> has no guard against a second call, and
/// <c>MassTransitHostedService.StopAsync</c> sets its stopped flag before it awaits the bus, so the second
/// caller returns at once. When that second caller is the factory, it disposes the host's
/// <c>IServiceProvider</c> while the bus is still draining in-flight messages (verified by decompiling
/// Microsoft.Extensions.Hosting 10.0 and MassTransit 8.5.10). The bus then never finishes stopping and keeps
/// consuming: a zombie consumer whose every message faults with ObjectDisposedException 'IServiceProvider'.
/// It only happens when the bus is busy at dispose time, so it shows up at random. TestHostTracker caught
/// it in the Spine and Phase3 fixtures.
/// </para>
/// <para>
/// This service is stopped first and stops MassTransit's hosted service itself, once. Both <c>StopAsync</c>
/// callers await that same stop, so neither reaches disposal before the bus has stopped. MassTransit's own
/// <c>StopAsync</c> is then a no-op.
/// </para>
/// </summary>
internal sealed class BusStopsBeforeHostDisposal(IServiceProvider services) : IHostedService
{
    private readonly Lock _gate = new();
    private Task? _stop;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Task stop;
        lock (_gate)
        {
            stop = _stop ??= StopBusAsync(cancellationToken);
        }

        // Honours the host's ShutdownTimeout: a bus that never stops still fails the TestHostTracker check.
        return stop.WaitAsync(cancellationToken);
    }

    private Task StopBusAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(services.GetServices<IHostedService>()
            .OfType<MassTransitHostedService>()
            .Select(s => s.StopAsync(cancellationToken)));
}

/// <summary>
/// Leak guard for the service hosts a fixture builds. Every <c>CreateFactory</c> registers its host here,
/// and a bus observer counts that host's bus starts and stops. At fixture teardown, after the fixture's own
/// hosts are disposed, any bus that started and never stopped is a host a test created and never disposed:
/// an extra consumer on the shared broker's durable queues for the rest of the run (the class of bug behind
/// the #284/#285 flakes). The teardown disposes those hosts and then fails, naming each one and the test
/// that created it. The check counts observer callbacks, so it does not depend on timing.
/// </summary>
internal sealed class TestHostTracker
{
    private readonly ConcurrentQueue<TrackedHost> _hosts = new();

    /// <summary>Starts tracking a host for <typeparamref name="TMarker"/>'s service, recording who created it.</summary>
    public TrackedHost Register<TMarker>()
    {
        var host = new TrackedHost(ServiceName(typeof(TMarker)), CreatedBy());
        _hosts.Enqueue(host);
        return host;
    }

    /// <summary>
    /// Disposes every tracked host whose bus is still running and returns a description of each, or null
    /// when every host was disposed. Call after the fixture has disposed the hosts it owns.
    /// </summary>
    public async Task<string?> DisposeLeakedAsync()
    {
        var leaked = _hosts.Where(h => h.Leaked).ToList();
        if (leaked.Count == 0)
        {
            return null;
        }

        var lines = leaked.Select(h => $"  - {h.Service} host (bus started {h.Started}x, stop requested {h.StopRequested}x, stopped {h.Stopped}x{h.StopFault}), created by {h.CreatedBy}").ToList();
        foreach (var host in leaked)
        {
            if (host.Factory is not null)
            {
                await host.Factory.DisposeAsync();
            }
        }

        return $"{leaked.Count} service host(s) were never disposed. Their buses kept consuming from the shared "
            + "broker's queues for the rest of the run. Dispose every factory a test creates (await using, or the "
            + "test class's IAsyncLifetime.DisposeAsync):" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    /// <summary>Throws when <see cref="DisposeLeakedAsync"/> reported leaks. Call after the containers are disposed.</summary>
    public static void ThrowIfLeaked(string? report)
    {
        if (report is not null)
        {
            throw new InvalidOperationException(report);
        }
    }

    // "ThreeCommerce.Catalog.Api.IApiMarker" -> "Catalog".
    private static string ServiceName(Type marker)
    {
        var parts = marker.Namespace?.Split('.') ?? [];
        return parts.Length >= 2 ? parts[1] : marker.FullName ?? marker.Name;
    }

    // The test-code frames that led to this factory, innermost first, without the fixture/tracker plumbing.
    // Async methods appear as their compiler state machine ("<Name>d__3" nested in the test class); they are
    // shown as Class.Name.
    private static string CreatedBy()
    {
        var assembly = typeof(TestHostTracker).Assembly;
        var frames = new StackTrace().GetFrames()
            .Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType is { } t && t.Assembly == assembly)
            .Select(m => Describe(m!))
            .Where(d => d is not null)
            .Distinct()
            .Take(3)
            .ToList();
        return frames.Count == 0 ? "(unknown caller)" : string.Join(" <- ", frames);
    }

    private static string? Describe(MethodBase method)
    {
        var type = method.DeclaringType!;
        var name = method.Name;
        if (type.Name.StartsWith('<') && type.DeclaringType is not null)
        {
            // Compiler-generated: async state machine "<Method>d__N" or closure "<>c__DisplayClass…".
            var end = type.Name.IndexOf('>', StringComparison.Ordinal);
            name = end > 1 ? type.Name[1..end] : name;
            type = type.DeclaringType;
        }

        if (type == typeof(TestHostTracker) || type == typeof(TestBusHosting) || type.Name.EndsWith("Fixture", StringComparison.Ordinal))
        {
            return null;
        }

        return $"{type.Name}.{name}";
    }
}

/// <summary>One fixture-built service host: its bus start/stop counts and the factory to dispose if it leaks.</summary>
internal sealed class TrackedHost(string service, string createdBy) : IBusObserver
{
    private int _started;
    private int _stopRequested;
    private int _stopped;
    private string? _stopFault;

    public string Service { get; } = service;

    public string CreatedBy { get; } = createdBy;

    public int Started => Volatile.Read(ref _started);

    public int Stopped => Volatile.Read(ref _stopped);

    public int StopRequested => Volatile.Read(ref _stopRequested);

    /// <summary>Why the bus's last stop faulted, if it did, formatted for the leak report.</summary>
    public string StopFault => Volatile.Read(ref _stopFault) is { } fault ? $", stop faulted: {fault}" : string.Empty;

    /// <summary>A bus that started more often than it stopped is still consuming.</summary>
    public bool Leaked => Started > Stopped;

    /// <summary>The host's factory, set by the fixture once built.</summary>
    public IAsyncDisposable? Factory { get; set; }

    public void PostCreate(IBus bus)
    {
    }

    public void CreateFaulted(Exception exception)
    {
    }

    public Task PreStart(IBus bus) => Task.CompletedTask;

    public Task PostStart(IBus bus, Task<BusReady> busReady)
    {
        Interlocked.Increment(ref _started);
        return Task.CompletedTask;
    }

    public Task StartFaulted(IBus bus, Exception exception) => Task.CompletedTask;

    public Task PreStop(IBus bus)
    {
        Interlocked.Increment(ref _stopRequested);
        return Task.CompletedTask;
    }

    public Task PostStop(IBus bus)
    {
        Interlocked.Increment(ref _stopped);
        return Task.CompletedTask;
    }

    public Task StopFaulted(IBus bus, Exception exception)
    {
        Volatile.Write(ref _stopFault, $"{exception.GetType().Name}: {exception.Message}");
        return Task.CompletedTask;
    }
}
