using MassTransit;
using Microsoft.Extensions.Logging;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Tests;

// Test doubles shared by the renewal / usage charge unit suites (RecurringChargeAmountTests,
// SubscriptionRenewalOutcomeTests): a recording rail, a one-provider registry, a fixed clock, a capturing logger and a
// recording publish endpoint.

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
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
    public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
    public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets) => null;
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
    public List<object> Published { get; } = [];

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
