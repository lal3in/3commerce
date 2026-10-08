using System.Collections.Concurrent;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Watches one running service host while a test races its consumers: every Warning-or-worse log line,
/// every consume/receive fault on its bus, how often each message id finished the receive pipeline, and the
/// depth of the named <c>_error</c> queues. <see cref="AssertNoRaceAsync"/> then proves the race left no trace:
/// no log line carrying one of the race signatures (23505, 40001, a constraint name…), no fault, no
/// <c>_error</c> growth. Attach it to the fixture's shared host rather than a second host, which would compete
/// for the same queues and take messages this probe never sees.
/// </summary>
public sealed class ConsumerRaceProbe : IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly string _rabbitMqUri;
    private readonly string[] _errorQueues;
    private readonly string[] _signatures;
    private readonly LogCapture _logs = new();
    private readonly Observer _observer = new();
    private ConnectHandle? _handle;
    private Dictionary<string, uint> _errorsBefore = [];

    private ConsumerRaceProbe(IServiceProvider services, string rabbitMqUri, string[] errorQueues, string[] signatures)
    {
        _services = services;
        _rabbitMqUri = rabbitMqUri;
        _errorQueues = errorQueues;
        _signatures = signatures;
    }

    /// <summary>Starts watching <paramref name="services"/>' host (its logger factory and its bus).</summary>
    /// <param name="errorQueues">The consumers' error queues: kebab-case endpoint name + <c>_error</c>.</param>
    /// <param name="signatures">Substrings that mark a race in a log line (case-insensitive).</param>
    public static async Task<ConsumerRaceProbe> StartAsync(
        IServiceProvider services, string rabbitMqUri, string[] errorQueues, string[] signatures)
    {
        var probe = new ConsumerRaceProbe(services, rabbitMqUri, errorQueues, signatures);
        // AddProvider also rewires the host's existing loggers; the capture switches itself off on dispose
        // (a provider cannot be removed from a running host).
        services.GetRequiredService<ILoggerFactory>().AddProvider(probe._logs);
        probe._handle = services.GetRequiredService<IBus>().ConnectReceiveObserver(probe._observer);
        probe._errorsBefore = await probe.ErrorQueueDepthsAsync();
        return probe;
    }

    /// <summary>How many times the message with this id has been through the whole receive pipeline
    /// (inbox, consumer, commit) on the watched host.</summary>
    public int ReceivedCount(Guid messageId) => _observer.ReceivedCount(messageId);

    /// <summary>Waits until every message id has finished the receive pipeline at least once.</summary>
    public Task WaitForReceivedAsync(IEnumerable<Guid> messageIds, string what)
    {
        var ids = messageIds.ToList();
        return WaitUntilAsync(() => Task.FromResult(ids.All(id => ReceivedCount(id) >= 1)), what);
    }

    public async Task AssertNoRaceAsync()
    {
        var raceLogs = _logs.Entries
            .Where(e => _signatures.Any(sig => e.Contains(sig, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.True(raceLogs.Count == 0, $"concurrent consumers collided:{Environment.NewLine}{string.Join(Environment.NewLine, raceLogs)}");
        Assert.True(_observer.Faults.IsEmpty, $"consumer faults:{Environment.NewLine}{string.Join(Environment.NewLine, _observer.Faults)}");

        var errorsAfter = await ErrorQueueDepthsAsync();
        foreach (var queue in _errorQueues)
        {
            Assert.True(errorsAfter[queue] == _errorsBefore[queue], $"{queue} grew from {_errorsBefore[queue]} to {errorsAfter[queue]}");
        }
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int timeoutSeconds = 30)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
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

    public ValueTask DisposeAsync()
    {
        _logs.Active = false;
        _handle?.Disconnect();
        return ValueTask.CompletedTask;
    }

    /// <summary>Message count of each error queue (0 when it was never created).</summary>
    private async Task<Dictionary<string, uint>> ErrorQueueDepthsAsync()
    {
        var factory = new ConnectionFactory { Uri = new Uri(_rabbitMqUri) };
        await using var connection = await factory.CreateConnectionAsync();
        var depths = new Dictionary<string, uint>();
        foreach (var queue in _errorQueues)
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

    private sealed class Observer : IReceiveObserver
    {
        private readonly ConcurrentDictionary<Guid, int> _received = new();

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

        public Task PostConsume<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType) where T : class =>
            Task.CompletedTask;

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

    /// <summary>Keeps every Warning-or-worse log line of the host (message + exception) while active.</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        private volatile bool _active = true;

        public bool Active
        {
            get => _active;
            set => _active = value;
        }

        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => owner.Active && logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    owner.Entries.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)} {exception}");
                }
            }
        }
    }
}
