using ThreeCommerce.BuildingBlocks.Contracts.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public static class StreamPrivacyGuard
{
    public static void ValidateForPublication<TPayload>(string topic, StreamEventEnvelope<TPayload> envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        if (envelope.PrivacyClass == StreamPrivacyClass.Restricted)
            throw new InvalidOperationException("Restricted stream payloads must not be published to Kafka.");

        if (envelope.TenantId is null && !IsPlatformGlobalTopic(topic))
            throw new InvalidOperationException($"Stream topic '{topic}' requires tenant metadata.");
    }

    private static bool IsPlatformGlobalTopic(string topic) => topic.Equals(StreamTopics.WorkflowRuns, StringComparison.Ordinal);
}
