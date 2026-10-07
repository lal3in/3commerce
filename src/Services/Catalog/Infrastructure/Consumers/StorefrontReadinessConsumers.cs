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
// ConcurrentMessageLimit = 1 on both endpoints, which also applies them in queue (publish) order.
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
            INSERT INTO catalog."StorefrontCarrierReadiness" ("StorefrontId", "TenantId", "HasActiveCarrier")
            VALUES ({m.StorefrontId}, {m.TenantId}, {m.HasActiveCarrier})
            ON CONFLICT ("StorefrontId") DO UPDATE
            SET "TenantId" = EXCLUDED."TenantId", "HasActiveCarrier" = EXCLUDED."HasActiveCarrier"
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
            INSERT INTO catalog."StorefrontPaymentReadiness" ("StorefrontId", "TenantId", "HasActivePaymentAccount")
            VALUES ({m.StorefrontId}, {m.TenantId}, {m.HasActivePaymentAccount})
            ON CONFLICT ("StorefrontId") DO UPDATE
            SET "TenantId" = EXCLUDED."TenantId", "HasActivePaymentAccount" = EXCLUDED."HasActivePaymentAccount"
            """,
            context.CancellationToken);
    }
}
