using System.Collections.Concurrent;
using MassTransit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Catalog.Infrastructure;
using CatalogApi = ThreeCommerce.Catalog.Api.IApiMarker;
using Readiness = ThreeCommerce.Catalog.Domain.StorefrontServiceReadiness;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Concurrency guard for Catalog's go-live readiness projection (ADR-0042). A new storefront's carrier and
/// payment readiness events typically arrive together; when both consumers read-then-inserted one shared row,
/// one of them failed with 23505 on PK_StorefrontServiceReadiness and only MassTransit's retry rescued it
/// (an error log per new storefront, and a fault to <c>_error</c> under tighter retry settings). Each signal now
/// has its own row written by a single upsert, so concurrent first writes never collide.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public sealed class StorefrontReadinessConcurrencyTests(Phase2Fixture fixture) : IAsyncLifetime
{
    // Kebab-case endpoint names of the two consumers (SetKebabCaseEndpointNameFormatter) + MassTransit's suffix.
    private static readonly string[] ErrorQueues =
        ["storefront-carrier-readiness_error", "storefront-payment-readiness_error"];

    // Signatures of the race in any log line or exception: unique violation, serialization failure, the PKs.
    private static readonly string[] RaceSignatures =
        ["23505", "40001", "could not serialize", "PK_StorefrontServiceReadiness", "PK_StorefrontCarrierReadiness", "PK_StorefrontPaymentReadiness"];

    private readonly LogCapture _logs = new();
    private readonly BusObserver _observer = new();
    private WebApplicationFactory<CatalogApi> _catalog = null!;
    private IBus _bus = null!;
    private Dictionary<string, uint> _errorsBefore = null!;

    public async Task InitializeAsync()
    {
        // One host only: the fixture starts the factory it returns, and a second (derived) host would compete
        // for the same queues, so messages it took would escape the observer below.
        _catalog = fixture.CreateCatalogFactory();
        _catalog.Services.GetRequiredService<ILoggerFactory>().AddProvider(_logs); // also rewires existing loggers
        _bus = _catalog.Services.GetRequiredService<IBus>();
        _bus.ConnectReceiveObserver(_observer);
        _errorsBefore = await ErrorQueueDepthsAsync();
    }

    public Task DisposeAsync()
    {
        _catalog.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Concurrent_first_carrier_and_payment_writes_for_new_storefronts_never_collide()
    {
        var tenant = Guid.CreateVersion7();
        // Varied flags, so "both columns right" can't pass by accident of defaults.
        var expected = Enumerable.Range(0, 20)
            .Select(i => (Storefront: Guid.CreateVersion7(), Carrier: i % 2 == 0, Payment: i % 3 != 0))
            .ToList();

        // Both signals for every storefront at once, through the bus (not a scoped outbox endpoint).
        await Task.WhenAll(expected.SelectMany(e => new[]
        {
            _bus.Publish(new StorefrontCarrierReadinessChanged(tenant, e.Storefront, e.Carrier)),
            _bus.Publish(new StorefrontPaymentReadinessChanged(tenant, e.Storefront, e.Payment)),
        }));

        await WaitUntilAsync(() => _observer.Consumed >= expected.Count * 2, "every readiness event consumed");
        var rows = await ReadAsync(expected.Select(e => e.Storefront));

        foreach (var e in expected)
        {
            Assert.True(rows.TryGetValue(e.Storefront, out var row), $"no readiness row for {e.Storefront}");
            Assert.Equal(tenant, row.TenantId);
            Assert.Equal(e.Carrier, row.HasActiveCarrier);
            Assert.Equal(e.Payment, row.HasActivePaymentAccount);
        }

        await AssertNoRaceAsync();
    }

    [Fact]
    public async Task A_burst_of_one_signal_for_one_storefront_applies_in_order_without_conflicts()
    {
        // Several changes of the SAME signal for one storefront back to back (a carrier configured, activated,
        // paused…): consumed in parallel they hit the same row under REPEATABLE READ (40001), and a retried
        // stale value could land last. Serial consumption per readiness endpoint applies them in queue order.
        var tenant = Guid.CreateVersion7();
        var storefront = Guid.CreateVersion7();
        bool[] carrier = [false, true, false, true, true, false, true];
        bool[] payment = [true, false, true, false, false, true, false];
        var ids = new List<Guid>();

        for (var i = 0; i < carrier.Length; i++)
        {
            Guid carrierId = NewId.NextGuid(), paymentId = NewId.NextGuid();
            ids.Add(carrierId);
            ids.Add(paymentId);
            await _bus.Publish(new StorefrontCarrierReadinessChanged(tenant, storefront, carrier[i]), c => c.MessageId = carrierId);
            await _bus.Publish(new StorefrontPaymentReadinessChanged(tenant, storefront, payment[i]), c => c.MessageId = paymentId);
        }

        await WaitUntilAsync(() => ids.All(id => _observer.ReceivedCount(id) >= 1), "every burst message processed");
        var row = (await ReadAsync([storefront]))[storefront];
        Assert.Equal(carrier[^1], row.HasActiveCarrier);
        Assert.Equal(payment[^1], row.HasActivePaymentAccount);

        await AssertNoRaceAsync();
    }

    [Fact]
    public async Task Redelivered_and_repeated_events_are_idempotent_and_never_clobber_the_other_signal()
    {
        var tenant = Guid.CreateVersion7();
        var storefront = Guid.CreateVersion7();
        var carrierMessageId = NewId.NextGuid();

        await Task.WhenAll(
            _bus.Publish(new StorefrontCarrierReadinessChanged(tenant, storefront, true), c => c.MessageId = carrierMessageId),
            _bus.Publish(new StorefrontPaymentReadinessChanged(tenant, storefront, true)));
        await WaitForRowAsync(storefront, carrier: true, payment: true);

        // Redelivery of the same message (inbox dedup) and a re-publish of the same truth (upsert) — the row stays.
        // PostReceive fires once the whole receive pipeline (inbox, consumer, commit) has finished with a message.
        var republishedId = NewId.NextGuid();
        await _bus.Publish(new StorefrontCarrierReadinessChanged(tenant, storefront, true), c => c.MessageId = carrierMessageId);
        await _bus.Publish(new StorefrontCarrierReadinessChanged(tenant, storefront, true), c => c.MessageId = republishedId);
        await WaitUntilAsync(() => _observer.ReceivedCount(carrierMessageId) >= 2, "the redelivered carrier message processed");
        await WaitUntilAsync(() => _observer.ReceivedCount(republishedId) >= 1, "the re-published carrier message processed");
        await WaitForRowAsync(storefront, carrier: true, payment: true);

        // Each consumer writes only its own signal: flipping one leaves the other untouched.
        await _bus.Publish(new StorefrontCarrierReadinessChanged(tenant, storefront, false));
        await WaitForRowAsync(storefront, carrier: false, payment: true);
        await _bus.Publish(new StorefrontPaymentReadinessChanged(tenant, storefront, false));
        await WaitForRowAsync(storefront, carrier: false, payment: false);

        await AssertNoRaceAsync();
    }

    private async Task AssertNoRaceAsync()
    {
        var raceLogs = _logs.Entries
            .Where(e => RaceSignatures.Any(sig => e.Contains(sig, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(raceLogs.Count == 0, $"readiness writes collided:{Environment.NewLine}{string.Join(Environment.NewLine, raceLogs)}");
        Assert.True(_observer.Faults.IsEmpty, $"consumer faults:{Environment.NewLine}{string.Join(Environment.NewLine, _observer.Faults)}");

        var errorsAfter = await ErrorQueueDepthsAsync();
        foreach (var queue in ErrorQueues)
        {
            Assert.True(errorsAfter[queue] == _errorsBefore[queue], $"{queue} grew from {_errorsBefore[queue]} to {errorsAfter[queue]}");
        }
    }

    private async Task WaitForRowAsync(Guid storefront, bool carrier, bool payment) =>
        await WaitUntilAsync(async () =>
        {
            var rows = await ReadAsync([storefront]);
            return rows.TryGetValue(storefront, out var r) && r.HasActiveCarrier == carrier && r.HasActivePaymentAccount == payment;
        }, $"readiness {storefront} = carrier:{carrier} payment:{payment}");

    private async Task<Dictionary<Guid, Readiness>> ReadAsync(IEnumerable<Guid> storefronts)
    {
        var ids = storefronts.ToList();
        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await db.StorefrontServiceReadiness.AsNoTracking()
            .Where(r => ids.Contains(r.StorefrontId))
            .ToDictionaryAsync(r => r.StorefrontId);
    }

    private static Task WaitUntilAsync(Func<bool> condition, string what) =>
        WaitUntilAsync(() => Task.FromResult(condition()), what);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {what}.");
    }

    /// <summary>Message count of each consumer's <c>_error</c> queue (0 when it was never created).</summary>
    private async Task<Dictionary<string, uint>> ErrorQueueDepthsAsync()
    {
        var factory = new ConnectionFactory { Uri = new Uri(fixture.RabbitMqUri) };
        await using var connection = await factory.CreateConnectionAsync();
        var depths = new Dictionary<string, uint>();
        foreach (var queue in ErrorQueues)
        {
            // A passive declare of a missing queue closes the channel (404), so use one channel per queue.
            await using var channel = await connection.CreateChannelAsync();
            try
            {
                depths[queue] = (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
            }
            catch (OperationInterruptedException)
            {
                depths[queue] = 0;
            }
        }

        return depths;
    }

    /// <summary>Counts readiness consumption and records every fault on the Catalog bus.</summary>
    private sealed class BusObserver : IReceiveObserver
    {
        private int _consumed;
        private readonly ConcurrentDictionary<Guid, int> _received = new();

        public int Consumed => Volatile.Read(ref _consumed);
        public ConcurrentQueue<string> Faults { get; } = new();

        public int ReceivedCount(Guid messageId) => _received.GetValueOrDefault(messageId);

        public Task PreReceive(ReceiveContext context) => Task.CompletedTask;

        public Task PostReceive(ReceiveContext context)
        {
            if (context.GetMessageId() is { } id)
            {
                _received.AddOrUpdate(id, 1, (_, n) => n + 1);
            }

            return Task.CompletedTask;
        }

        public Task PostConsume<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType) where T : class
        {
            if (context.Message is StorefrontCarrierReadinessChanged or StorefrontPaymentReadinessChanged)
            {
                Interlocked.Increment(ref _consumed);
            }

            return Task.CompletedTask;
        }

        public Task ConsumeFault<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType, Exception exception) where T : class
        {
            Faults.Enqueue($"{typeof(T).Name} via {consumerType}: {exception}");
            return Task.CompletedTask;
        }

        public Task ReceiveFault(ReceiveContext context, Exception exception)
        {
            Faults.Enqueue($"receive fault on {context.InputAddress}: {exception}");
            return Task.CompletedTask;
        }
    }

    /// <summary>Keeps every Warning-or-worse log line of the Catalog host (message + exception).</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    entries.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)} {exception}");
                }
            }
        }
    }
}
