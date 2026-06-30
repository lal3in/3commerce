namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed record StreamDeadLetterMessage(
    string SourceTopic,
    string DeadLetterTopic,
    string Key,
    long Offset,
    string ErrorType,
    string ErrorMessage,
    string OriginalJson,
    IReadOnlyDictionary<string, string> Headers);

public interface IStreamDeadLetterSink
{
    public Task SendAsync(StreamDeadLetterMessage message, CancellationToken cancellationToken = default);
}

public sealed class InMemoryStreamDeadLetterSink : IStreamDeadLetterSink
{
    private readonly List<StreamDeadLetterMessage> messages = [];
    private readonly Lock gate = new();

    public IReadOnlyList<StreamDeadLetterMessage> Messages
    {
        get
        {
            lock (gate)
            {
                return messages.ToArray();
            }
        }
    }

    public Task SendAsync(StreamDeadLetterMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            messages.Add(message);
        }

        return Task.CompletedTask;
    }
}
