using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Consumers;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Tests;

// Test doubles shared by the renewal / usage charge unit suites (RecurringChargeAmountTests,
// SubscriptionRenewalOutcomeTests): a recording rail, a one-provider registry, a fixed clock, a capturing logger and a
// recording publish endpoint.

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Builds the usage-charge service (<see cref="UsageChargeBilling"/>) and its requests over a test db.</summary>
internal static class UsageBilling
{
    public static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static UsageChargeBilling For(
        PaymentsDbContext db, IPaymentProvider provider, TimeProvider? time = null, RecordingPublisher? publisher = null,
        ILogger<UsageChargeBilling>? log = null) =>
        new(db, new SingleProviderRegistry(provider),
            new PaymentModeResolver(PaymentTestSupport.Config(("Payments:Mode", "Sandbox")), PaymentTestSupport.Env(Environments.Development)),
            time ?? TimeProvider.System, log ?? new ListLogger<UsageChargeBilling>(), publisher ?? new RecordingPublisher());

    public static UsageChargeRequest Request(
        Guid? storefrontId, long amountMinor, string reference, UsageChargeKind kind = UsageChargeKind.Overage, long quantity = 20,
        string currency = "USD") =>
        new(TenantId, storefrontId, "usage@example.com", kind, MeterType.Token, quantity, 0, amountMinor, currency, reference,
            $"Usage {kind} — {MeterType.Token} × {quantity}");
}

internal sealed class RecordingProvider : IPaymentProvider
{
    public List<PaymentRequest> Requests { get; } = [];

    /// <summary>The outcome answered once <see cref="Outcomes"/> is exhausted.</summary>
    public PaymentOutcome Outcome { get; set; } = PaymentOutcome.Succeeded;

    /// <summary>Per-call outcomes, answered in order before falling back to <see cref="Outcome"/>.</summary>
    public Queue<PaymentOutcome> Outcomes { get; } = new();

    /// <summary>Throw on this many first calls (a provider outage), recording the request first.</summary>
    public int ThrowFirst { get; set; }

    /// <summary>Called with the 1-based call number before answering; may throw to simulate a failure.</summary>
    public Action<int>? OnAuthorize { get; init; }

    public string ProviderKey => "stripe";

    public Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        OnAuthorize?.Invoke(Requests.Count);
        if (ThrowFirst > 0)
        {
            ThrowFirst--;
            throw new HttpRequestException("provider unavailable");
        }

        var outcome = Outcomes.Count > 0 ? Outcomes.Dequeue() : Outcome;
        var error = outcome switch
        {
            PaymentOutcome.RequiresAction => new PaymentError(PaymentErrorCode.AuthenticationRequired, "3DS required.", Retryable: true),
            PaymentOutcome.Failed => new PaymentError(PaymentErrorCode.CardDeclined, "Card declined.", Retryable: false),
            _ => null,
        };
        return Task.FromResult(new PaymentResponse($"pi_rec_{Requests.Count}", null, outcome, error));
    }

    public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) => throw new NotSupportedException();
    public Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct) => throw new NotSupportedException();
    public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct) => throw new NotSupportedException();
    /// <summary>Refunds the rail was asked for (intent, amount, idempotency key); every one succeeds.</summary>
    public List<(string Intent, long AmountMinor, string Key)> Refunds { get; } = [];

    public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct)
    {
        Refunds.Add((paymentIntentId, amountMinor, idempotencyKey));
        return Task.FromResult(new ProviderRefundResult($"re_rec_{Refunds.Count}", true));
    }

    public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets) => null;
}

/// <summary>A dedupe store that remembers nothing: the Postgres/EF WebhookInbox is the guarantee under test.</summary>
internal sealed class NoDedupe : ThreeCommerce.BuildingBlocks.Infrastructure.Redis.IDedupeStore
{
    public Task<bool> IsProcessedAsync(string key, CancellationToken ct) => Task.FromResult(false);
    public Task MarkProcessedAsync(string key, TimeSpan ttl, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class SingleProviderRegistry(IPaymentProvider provider) : IPaymentProviderRegistry
{
    public IPaymentProvider Resolve(PaymentAccountSnapshot account) => provider;
    public IPaymentProvider ResolveDefault() => provider;
    public IPaymentProvider ResolveByKey(string providerKey) => provider;
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>Records every published message (the real one is the scoped bus-outbox endpoint).</summary>
internal sealed class RecordingPublisher : IPublishEndpoint
{
    /// <summary>What was published; pass a shared list to collect across several service instances.</summary>
    public List<object> Published { get; init; } = [];

    public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class { Published.Add(message); return Task.CompletedTask; }
    public Task Publish<T>(T message, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class { Published.Add(message); return Task.CompletedTask; }
    public Task Publish<T>(T message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class { Published.Add(message); return Task.CompletedTask; }
    public Task Publish(object message, CancellationToken cancellationToken = default) { Published.Add(message); return Task.CompletedTask; }
    public Task Publish(object message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) { Published.Add(message); return Task.CompletedTask; }
    public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default) { Published.Add(message); return Task.CompletedTask; }
    public Task Publish(object message, Type messageType, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) { Published.Add(message); return Task.CompletedTask; }
    public Task Publish<T>(object values, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
    public Task Publish<T>(object values, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
    public Task Publish<T>(object values, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
    public ConnectHandle ConnectPublishObserver(IPublishObserver observer) => throw new NotSupportedException();
}
