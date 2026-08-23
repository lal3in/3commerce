using MassTransit;

namespace ThreeCommerce.Entity.Tests;

/// <summary>
/// Minimal no-op <see cref="IPublishEndpoint"/> for unit tests that construct services which publish
/// contract events (e.g. <c>SupplierOnboardingService</c>) but whose behaviour under test does not
/// depend on the publish. Captures typed publishes so a test can assert them when it needs to.
/// </summary>
public sealed class NoopPublishEndpoint : IPublishEndpoint
{
    public List<object> Published { get; } = [];

    public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class
    {
        Published.Add(message);
        return Task.CompletedTask;
    }

    public Task Publish<T>(T message, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class =>
        Publish(message, cancellationToken);

    public Task Publish<T>(T message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class =>
        Publish(message, cancellationToken);

    public Task Publish(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task Publish(object message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task Publish(object message, Type messageType, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task Publish<T>(object values, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();

    public Task Publish<T>(object values, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();

    public Task Publish<T>(object values, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();

    public ConnectHandle ConnectPublishObserver(IPublishObserver observer) => throw new NotSupportedException();
}
