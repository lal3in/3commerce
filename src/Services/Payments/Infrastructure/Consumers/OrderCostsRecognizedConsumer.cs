using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Wires the previously-dormant COGS accrual (phase 1): when Ordering reports an order's per-supplier
/// cost of goods, book one <see cref="SupplierPayable"/> per supplier plus its balanced accrual entry
/// (Dr expense.cogs[.store-{id}] / Cr liability.supplier_payable). The debit is the supplier's NET
/// payable (gross less the active policy's commission); when no policy exists the platform owes the full
/// gross (commission 0). Idempotent by OrderId — a redelivered event accrues nothing extra.
/// </summary>
public sealed class OrderCostsRecognizedConsumer(
    PaymentsDbContext db, TimeProvider time) : IConsumer<OrderCostsRecognized>
{
    public async Task Consume(ConsumeContext<OrderCostsRecognized> context)
    {
        var msg = context.Message;

        if (await db.SupplierPayables.AnyAsync(p => p.OrderId == msg.OrderId, context.CancellationToken))
        {
            return; // already accrued
        }

        var now = time.GetUtcNow();
        var cogsAccount = msg.StorefrontId is { } sid ? Accounts.CogsStoreFor(sid) : null;

        foreach (var item in msg.Items)
        {
            if (item.CostMinor <= 0 || item.SupplierEntityId == Guid.Empty)
            {
                continue;
            }

            // Prefer the tenant/supplier's configured commission policy; absent one the platform owes the
            // full gross (an in-memory zero-commission default — never persisted, so it can't shadow a
            // policy an operator adds later).
            var policy = await db.SupplierPayablePolicies
                .Where(p => p.TenantId == msg.TenantId && p.SupplierEntityId == item.SupplierEntityId && p.Active)
                .OrderByDescending(p => p.Id)
                .FirstOrDefaultAsync(context.CancellationToken)
                ?? new SupplierPayablePolicy
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = msg.TenantId,
                    SupplierEntityId = item.SupplierEntityId,
                    CommissionBps = 0,
                    Cadence = PayoutCadence.Manual,
                };

            var payable = SupplierPayable.Create(
                msg.TenantId, item.SupplierEntityId, msg.OrderId, item.CostMinor, msg.Currency, policy, now);

            db.SupplierPayables.Add(payable);
            db.JournalEntries.Add(payable.ToAccrualEntry(now, cogsAccount));
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
