using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;

namespace ThreeCommerce.Catalog.Infrastructure.Consumers;

// Mirror the cross-service go-live signals onto Catalog's readiness read model (ADR-0042). Each event carries
// the current truth for its signal, so applying it is idempotent: redelivery writes the same values.
//
// Concurrency: the carrier and payment signals for a new storefront usually arrive together, so each consumer
// owns its own table (StorefrontCarrierReadiness / StorefrontPaymentReadiness) and writes it with one atomic
// INSERT … ON CONFLICT DO UPDATE — never read-then-insert. A shared row can't be made safe here: the EF outbox
// runs each consumer in a REPEATABLE READ transaction whose snapshot predates the write, so a concurrent first
// insert of the same row fails with 23505 (read-then-insert) or 40001 (upsert/update) and costs a retry.
// For the same reason two events of ONE signal for one storefront must not run in parallel; Program.cs sets
// ConcurrentMessageLimit = 1 on both endpoints, which also applies them in queue order.
//
// Queue order is NOT publish order: each publishing request is its own outbox batch, and MassTransit's bus-outbox
// delivery service sends up to six batches in parallel after a burst (RequestRateAlgorithm), so an earlier
// "not active" can reach the queue after a later "active" — and a blind upsert then leaves the storefront stuck
// on the stale value. Each event carries AsOf (when the publisher read the truth); the upsert's WHERE keeps the
// newer copy (ties apply). A null AsOf (an event from a publisher older than the field) applies as before.
// Raw SQL is schema-qualified (ADR-0022); Catalog has no RLS, so no tenant scope is needed.

/// <summary>Projects <see cref="StorefrontCarrierReadinessChanged"/> onto <c>catalog."StorefrontCarrierReadiness"</c>.</summary>
public sealed class StorefrontCarrierReadinessConsumer(CatalogDbContext db)
    : IConsumer<StorefrontCarrierReadinessChanged>
{
    public async Task Consume(ConsumeContext<StorefrontCarrierReadinessChanged> context)
    {
        var m = context.Message;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO catalog."StorefrontCarrierReadiness" AS r ("StorefrontId", "TenantId", "HasActiveCarrier", "AsOf")
            VALUES ({m.StorefrontId}, {m.TenantId}, {m.HasActiveCarrier}, {m.AsOf?.ToUniversalTime()}::timestamptz)
            ON CONFLICT ("StorefrontId") DO UPDATE
            SET "TenantId" = EXCLUDED."TenantId", "HasActiveCarrier" = EXCLUDED."HasActiveCarrier",
                "AsOf" = COALESCE(EXCLUDED."AsOf", r."AsOf")
            WHERE EXCLUDED."AsOf" IS NULL OR r."AsOf" IS NULL OR r."AsOf" <= EXCLUDED."AsOf"
            """,
            context.CancellationToken);
    }
}

/// <summary>Projects <see cref="StorefrontPaymentReadinessChanged"/> onto <c>catalog."StorefrontPaymentReadiness"</c>.</summary>
public sealed class StorefrontPaymentReadinessConsumer(CatalogDbContext db)
    : IConsumer<StorefrontPaymentReadinessChanged>
{
    public async Task Consume(ConsumeContext<StorefrontPaymentReadinessChanged> context)
    {
        var m = context.Message;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO catalog."StorefrontPaymentReadiness" AS r ("StorefrontId", "TenantId", "HasActivePaymentAccount", "AsOf")
            VALUES ({m.StorefrontId}, {m.TenantId}, {m.HasActivePaymentAccount}, {m.AsOf?.ToUniversalTime()}::timestamptz)
            ON CONFLICT ("StorefrontId") DO UPDATE
            SET "TenantId" = EXCLUDED."TenantId", "HasActivePaymentAccount" = EXCLUDED."HasActivePaymentAccount",
                "AsOf" = COALESCE(EXCLUDED."AsOf", r."AsOf")
            WHERE EXCLUDED."AsOf" IS NULL OR r."AsOf" IS NULL OR r."AsOf" <= EXCLUDED."AsOf"
            """,
            context.CancellationToken);
    }
}
