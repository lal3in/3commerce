using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

/// <summary>
/// Records an admin mutation to the central audit timeline (mt6_1). Publish-only: the reusable producer
/// for services that don't keep a local hash-chained audit table. Call it in the same unit of work as the
/// mutation, BEFORE SaveChangesAsync, so the <see cref="AuditEntryRecorded"/> commits atomically via the
/// bus outbox. Recording must NEVER break the mutation it records, so failures are swallowed and logged.
/// (Services that keep an authoritative local chain — e.g. Entity — use <see cref="AuditRecorder"/> instead.)
/// </summary>
public interface IAuditRecorder
{
    public Task RecordAsync(AuditDraft draft, CancellationToken ct);
}

/// <summary>The owning service's name, stamped on every entry it emits (mt6_1).</summary>
public sealed record AuditSource(string Value);

/// <summary>
/// Publish-only <see cref="IAuditRecorder"/> (mt6_1). Self-hashes a standalone entry (PrevHash = GENESIS)
/// and publishes <see cref="AuditEntryRecorded"/> through the bus outbox. There is no local chain here, so
/// the entry's Sequence is not a per-tenant position — the central projection dedupes by Hash, not sequence.
/// </summary>
public sealed class AuditEmitter(
    IPublishEndpoint publisher, AuditSource source, TimeProvider clock, ILogger<AuditEmitter> logger) : IAuditRecorder
{
    public async Task RecordAsync(AuditDraft draft, CancellationToken ct)
    {
        try
        {
            var entry = AuditChain.Append(null, draft, clock.GetUtcNow());
            await publisher.Publish(new AuditEntryRecorded(
                entry.TenantId, entry.Sequence, entry.OccurredAt, entry.ActorId, entry.ActorRole,
                entry.Action, entry.ResourceType, entry.ResourceId, entry.Outcome.ToString(), entry.Summary, entry.Hash), ct);
        }
        catch (Exception ex)
        {
            // Auditing must never break the mutation it records — log and move on.
            logger.LogError(ex, "Failed to record audit entry {Action} on {ResourceType}/{ResourceId} (source {Source})",
                draft.Action, draft.ResourceType, draft.ResourceId, source.Value);
        }
    }
}

public static class AuditRecorderRegistration
{
    /// <summary>
    /// Register the publish-only audit recorder (mt6_1) for a service that emits admin-mutation audit
    /// entries to the central timeline. <paramref name="source"/> names the owning service.
    /// </summary>
    public static IServiceCollection AddAuditRecorder(this IServiceCollection services, string source)
    {
        services.AddSingleton(new AuditSource(source));
        services.AddScoped<IAuditRecorder, AuditEmitter>();
        return services;
    }
}
