namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed record StreamReplayRecord(
    string Topic,
    string Key,
    long Offset,
    string Json,
    IReadOnlyDictionary<string, string> Headers);
