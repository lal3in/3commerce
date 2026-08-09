using System.Diagnostics.Metrics;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Redis;

/// <summary>
/// Application-level Redis fast-path metrics (ADR-0044), exported through the existing OTEL pipeline
/// (registered via <c>AddMeter</c> in OtelExtensions → collector → Prometheus → Grafana). Server-level
/// metrics (memory, evictions, keyspace, replication) come separately from redis_exporter. Mirrors the
/// <see cref="ThreeCommerce.BuildingBlocks.Infrastructure.Streams.StreamMetrics"/> pattern.
/// </summary>
public static class RedisMetrics
{
    public const string MeterName = "3commerce.redis";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> RateLimitDecisions = Meter.CreateCounter<long>("redis_ratelimit_decisions_total");
    private static readonly Counter<long> IdempotencyDedupe = Meter.CreateCounter<long>("redis_idempotency_dedupe_total");
    private static readonly Counter<long> SessionCacheHits = Meter.CreateCounter<long>("redis_session_cache_hits_total");
    private static readonly Counter<long> SessionCacheMisses = Meter.CreateCounter<long>("redis_session_cache_misses_total");
    private static readonly Counter<long> MfaChallenge = Meter.CreateCounter<long>("redis_mfa_challenge_total");
    private static readonly Counter<long> Unavailable = Meter.CreateCounter<long>("redis_unavailable_total");
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>("redis_operation_duration_seconds");

    /// <param name="result">allow | reject</param>
    /// <param name="partitionKind">auth | any</param>
    public static void RecordRateLimitDecision(string result, string partitionKind) =>
        RateLimitDecisions.Add(1, new KeyValuePair<string, object?>("result", result), new KeyValuePair<string, object?>("partition_kind", partitionKind));

    /// <param name="result">hit | miss | conflict</param>
    public static void RecordIdempotencyDedupe(string result) =>
        IdempotencyDedupe.Add(1, new KeyValuePair<string, object?>("result", result));

    public static void RecordSessionCacheHit() => SessionCacheHits.Add(1);

    public static void RecordSessionCacheMiss() => SessionCacheMisses.Add(1);

    /// <param name="result">issued | verified | expired | locked</param>
    public static void RecordMfaChallenge(string result) =>
        MfaChallenge.Add(1, new KeyValuePair<string, object?>("result", result));

    /// <param name="concern">ratelimit | idempotency | session | mfa</param>
    public static void RecordUnavailable(string concern) =>
        Unavailable.Add(1, new KeyValuePair<string, object?>("concern", concern));

    public static void RecordOperationDuration(string op, double seconds) =>
        OperationDuration.Record(seconds, new KeyValuePair<string, object?>("op", op));
}
