using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Testcontainers.RabbitMq;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Every service host waits for its MassTransit bus before it serves (<c>AddServiceBus</c> sets
/// <see cref="MassTransitHostOptions.WaitUntilStarted"/> with a bounded start). Unit lane: both overloads configure
/// it, and a host whose broker is unreachable fails its start at the bound instead of hanging or starting
/// half-ready.
/// </summary>
public sealed class BusStartupTests
{
    [Fact]
    public void Both_AddServiceBus_overloads_make_the_host_wait_for_a_bounded_bus_start()
    {
        foreach (var options in new[] { HostOptions(withOutbox: false), HostOptions(withOutbox: true) })
        {
            Assert.True(options.WaitUntilStarted);
            Assert.Equal(TimeSpan.FromSeconds(MassTransitExtensions.DefaultStartTimeoutSeconds), options.StartTimeout);
        }

        var configured = HostOptions(withOutbox: false, (MassTransitExtensions.StartTimeoutSetting, "7"));
        Assert.Equal(TimeSpan.FromSeconds(7), configured.StartTimeout);
    }

    [Fact]
    public void A_non_positive_start_timeout_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HostOptions(withOutbox: false, (MassTransitExtensions.StartTimeoutSetting, "0")));
        Assert.Contains(MassTransitExtensions.StartTimeoutSetting, ex.Message);
    }

    [Fact]
    public async Task Host_start_fails_at_the_bound_when_the_broker_is_unreachable()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:RabbitMq"] = $"amqp://guest:guest@127.0.0.1:{ClosedPort()}";
        builder.Configuration[MassTransitExtensions.StartTimeoutSetting] = "3";
        builder.Services.AddSingleton(new BusStartupProbes());
        builder.Services.AddServiceBus(builder.Configuration, bus => bus.AddConsumer<BusStartupProbeConsumer>());
        using var host = builder.Build();

        var clock = Stopwatch.StartNew();
        var ex = await Record.ExceptionAsync(() => host.StartAsync());

        // MassTransit retried the connection until the bound, then cancelled the start ("Bus start canceled"),
        // stopped the bus and rethrew the connection failure: the host never reports started, so the process
        // exits naming the broker instead of serving without a bus.
        var unreachable = Assert.IsType<RabbitMqConnectionException>(ex);
        Assert.Contains("Broker unreachable", unreachable.Message);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60));
        Assert.Equal(BusHealthStatus.Unhealthy, host.Services.GetRequiredService<IBusControl>().CheckHealth().Status);
    }

    private static MassTransitHostOptions HostOptions(bool withOutbox, params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();
        var services = new ServiceCollection();
        if (withOutbox)
        {
            services.AddServiceBus<BusStartupDbContext>(configuration);
        }
        else
        {
            services.AddServiceBus(configuration);
        }

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<MassTransitHostOptions>>().Value;
    }

    // A port nothing listens on: bind an ephemeral one, then release it.
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class BusStartupDbContext(DbContextOptions<BusStartupDbContext> options) : DbContext(options);
}

/// <summary>
/// The same guarantee against a real broker: a host's start returns only once its receive endpoints are bound,
/// so a message published the moment it starts — on a broker that has never seen the queue — is consumed
/// (before, the start returned at once and RabbitMQ dropped a publish that beat the binding, PR #284). And a
/// broker that is still booting when the service starts is waited for, not failed on.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BusStartupIntegrationTests
{
    [Fact]
    public async Task A_publish_right_after_host_start_reaches_the_consumer_on_a_fresh_broker()
    {
        await using var broker = Broker();
        await broker.StartAsync();

        // Another service's bus, already running — the publisher in the #284 flake (Payments publishing while the
        // Notifications host starts). Its publish goes straight to the exchange, whether or not a queue is bound.
        var publisher = Bus.Factory.CreateUsingRabbitMq(cfg => cfg.Host(new Uri(broker.GetConnectionString())));
        await publisher.StartAsync();
        // Warm the publisher (exchange declared, channel open) so the real publish below leaves at once. No queue
        // is bound yet, so RabbitMQ drops this one — exactly what happens to any publish that beats the binding.
        await publisher.Publish(new BusStartupProbe(Guid.NewGuid()));
        using var host = ServiceHost(broker.GetConnectionString(), startTimeoutSeconds: 60);
        try
        {
            await host.StartAsync();

            var id = Guid.NewGuid();
            await publisher.Publish(new BusStartupProbe(id));

            // Started means ready: the bus health MassTransit adds to /health/ready is already Healthy.
            var health = await host.Services.GetRequiredService<HealthCheckService>()
                .CheckHealthAsync(r => r.Tags.Contains("masstransit"));
            Assert.Equal(HealthStatus.Healthy, health.Status);

            await host.Services.GetRequiredService<BusStartupProbes>().WaitForAsync(id, TimeSpan.FromSeconds(20));
        }
        finally
        {
            await host.StopAsync();
            await publisher.StopAsync();
        }
    }

    [Fact]
    public async Task A_host_started_before_the_broker_waits_for_it_then_serves()
    {
        var port = UnusedPortBelowEphemeralRanges();
        await using var broker = Broker(port);

        using var host = ServiceHost($"amqp://busstart:busstart@127.0.0.1:{port}", startTimeoutSeconds: 120);
        var start = host.StartAsync();
        try
        {
            // The broker is not up yet: the host must still be starting (retrying), not started and not failed.
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(start.IsCompleted,
                $"host start completed without a broker: {start.Status} {start.Exception?.GetBaseException().Message}");

            // Each step names itself on failure: this test failed once in a full integration run with only its
            // [FAIL] line captured, and which step broke is most of the diagnosis.
            try
            {
                await broker.StartAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"broker container did not start on host port {port} (is the port taken where Docker binds it?): {ex.GetBaseException().Message}", ex);
            }

            var waited = Stopwatch.StartNew();
            try
            {
                await start.WaitAsync(TimeSpan.FromSeconds(110));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"host start did not complete {waited.Elapsed.TotalSeconds:F0} s after the broker came up ({start.Status}): {ex.GetBaseException().Message}", ex);
            }

            var id = Guid.NewGuid();
            await host.Services.GetRequiredService<IBus>().Publish(new BusStartupProbe(id));
            await host.Services.GetRequiredService<BusStartupProbes>().WaitForAsync(id, TimeSpan.FromSeconds(20));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static RabbitMqContainer Broker(int? hostPort = null)
    {
        var builder = new RabbitMqBuilder("rabbitmq:4").WithUsername("busstart").WithPassword("busstart");
        return (hostPort is { } port ? builder.WithPortBinding(port, 5672) : builder).Build();
    }

    // A Notifications-style host on the real AddServiceBus, with a probe consumer whose queue no broker has seen.
    private static IHost ServiceHost(string rabbitMqUri, int startTimeoutSeconds)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:RabbitMq"] = rabbitMqUri;
        builder.Configuration[MassTransitExtensions.StartTimeoutSetting] = startTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        builder.Services.AddSingleton(new BusStartupProbes());
        builder.Services.AddHealthChecks();
        builder.Services.AddServiceBus(builder.Configuration, bus => bus.AddConsumer<BusStartupProbeConsumer>());
        return builder.Build();
    }

    // The broker's host port has to be chosen before the broker exists (the host is pointed at it first), so Docker
    // cannot pick it. A port-0 pick (an ephemeral port free on this machine at that instant) was flaky: Docker binds
    // the port in ITS network namespace — inside the Linux VM on macOS (colima/Docker Desktop), whose ephemeral
    // range 32768–60999 overlaps macOS's 49152–65535 — and seconds later. Every connection the suite makes to a
    // container is relayed inside the VM (port forwarder + docker-proxy) from a random VM ephemeral port that then
    // sits in TIME_WAIT, so late in a full run the pick could already be held there, invisible to a check on the
    // Mac, and the broker's start failed with "address already in use". A port below every ephemeral range (Linux
    // 32768+, macOS/Windows 49152+) is never handed to an outgoing connection, nor to a container Docker publishes
    // on a random port, so only an explicit listener could hold it — and that is checked here on the loopback.
    private static int UnusedPortBelowEphemeralRanges()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var port = Random.Shared.Next(20000, 32768);
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                return port;
            }
            catch (SocketException)
            {
                // An explicit listener holds it; try another.
            }
            finally
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException("No port in 20000–32767 is free on the loopback.");
    }
}

public sealed record BusStartupProbe(Guid Id);

/// <summary>Test-only consumer; its queue (bus-startup-probe) exists on no broker until a test host starts.</summary>
public sealed class BusStartupProbeConsumer(BusStartupProbes probes) : IConsumer<BusStartupProbe>
{
    public Task Consume(ConsumeContext<BusStartupProbe> context)
    {
        probes.Received(context.Message.Id);
        return Task.CompletedTask;
    }
}

public sealed class BusStartupProbes
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _seen = new();

    public void Received(Guid id) => Slot(id).TrySetResult();

    public async Task WaitForAsync(Guid id, TimeSpan timeout)
    {
        try
        {
            await Slot(id).Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"BusStartupProbe {id} was never consumed within {timeout} — published before the queue was bound?");
        }
    }

    private TaskCompletionSource Slot(Guid id) =>
        _seen.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}
