namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public sealed record StreamPublishResult(string Topic, string Key, long? Offset);
