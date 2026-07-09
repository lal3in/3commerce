using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

namespace ThreeCommerce.Entity.Tests;

/// <summary>
/// mr_8: the publish-only audit producer (AuditEmitter) every service without a local audit table uses.
/// It must publish a self-consistent AuditEntryRecorded (hash verifies against the payload) and must
/// NEVER let a recording failure break the mutation it records.
/// </summary>
public class AuditEmitterTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();

    [Fact]
    public async Task Publishes_a_self_consistent_entry_with_actor_and_outcome()
    {
        var publisher = new FakePublishEndpoint();
        var emitter = new AuditEmitter(publisher, new AuditSource("payments"), TimeProvider.System, NullLogger<AuditEmitter>.Instance);

        await emitter.RecordAsync(AuditCategories.Mutation(
            Tenant, Actor, "admin", "PaymentAccount", "acc-1", "payments.payment_account.create", "Main"), default);

        var published = Assert.IsType<AuditEntryRecorded>(Assert.Single(publisher.Published));
        Assert.Equal(Tenant, published.TenantId);
        Assert.Equal(Actor, published.ActorId);
        Assert.Equal("admin", published.ActorRole);
        Assert.Equal("payments.payment_account.create", published.Action);
        Assert.Equal("PaymentAccount", published.ResourceType);
        Assert.Equal("acc-1", published.ResourceId);
        Assert.Equal("Success", published.Outcome);
        Assert.Equal("Main", published.Summary);

        // The hash is the standalone chain hash over the same fields — recompute and compare.
        var recomputed = new AuditEntry
        {
            Id = Guid.Empty,
            TenantId = published.TenantId,
            Sequence = published.Sequence,
            OccurredAt = published.OccurredAt,
            ActorId = published.ActorId,
            ActorRole = published.ActorRole,
            Action = published.Action,
            ResourceType = published.ResourceType,
            ResourceId = published.ResourceId,
            Outcome = AuditOutcome.Success,
            Summary = published.Summary,
            PrevHash = AuditEntry.Genesis,
            Hash = string.Empty,
        };
        Assert.Equal(recomputed.ComputeHash(), published.Hash);
    }

    [Fact]
    public async Task A_failing_publisher_never_breaks_the_mutation()
    {
        var emitter = new AuditEmitter(
            new FakePublishEndpoint(throwOnPublish: true), new AuditSource("payments"), TimeProvider.System, NullLogger<AuditEmitter>.Instance);

        // Must not throw — audit is best-effort and the mutation must proceed.
        await emitter.RecordAsync(AuditCategories.Mutation(
            Tenant, Actor, "admin", "PaymentAccount", "acc-1", "payments.payment_account.create"), default);
    }

    /// <summary>Minimal fake: captures typed publishes, or throws when <c>Throw</c> to simulate a dead broker.</summary>
    private sealed class FakePublishEndpoint(bool throwOnPublish = false) : IPublishEndpoint
    {
        public readonly List<object> Published = [];

        public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class
        {
            if (throwOnPublish)
            {
                throw new InvalidOperationException("broker down");
            }

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
}
