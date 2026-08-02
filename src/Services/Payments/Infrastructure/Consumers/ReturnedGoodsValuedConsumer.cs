using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Corrects an order's COGS accrual when an RMA return is dispositioned (phase 1). Ordering values the
/// returned goods (gross supplier cost) and reports the disposition; this consumer books the ledger
/// correction against the accrual that <see cref="OrderCostsRecognizedConsumer"/> posted:
/// <list type="bullet">
///   <item>Restock → reverse it (Dr liability.supplier_payable / Cr expense.cogs[.store-{id}]) — a
///   restocked unit re-accrues COGS on resale, so without the reversal it double-expenses.</item>
///   <item>Storage → reclass it to a write-off (Dr expense.writeoffs[.store-{id}] /
///   Cr expense.cogs[.store-{id}]) — total expense unchanged, loss surfaces as its own P&amp;L line.</item>
/// </list>
/// GUARDED to only post when a COGS accrual actually exists for the order; otherwise the credit side
/// would dangle. Idempotent by the reference <c>{RmaId}:{Revision}</c>. A disposition edit arrives with
/// a higher revision: the previous revision's posting is reversed first (a faithful line-swap), then the
/// new one is applied, so the books always reflect the CURRENT disposition and every entry balances.
/// </summary>
public sealed class ReturnedGoodsValuedConsumer(
    PaymentsDbContext db, TimeProvider time, ILogger<ReturnedGoodsValuedConsumer> logger) : IConsumer<ReturnedGoodsValued>
{
    // RmaDispositionKind on the wire (numeric enum, AGENTS.md).
    private const int Restock = 1;

    public async Task Consume(ConsumeContext<ReturnedGoodsValued> context)
    {
        var msg = context.Message;
        var reference = $"{msg.RmaId}:{msg.Revision}";

        if (await db.JournalEntries.AnyAsync(e => e.Reference == reference, context.CancellationToken))
        {
            return; // this revision already posted
        }

        // GUARD: only correct COGS that was actually accrued. An RMA on an order with no supplier cost
        // (nothing accrued) has nothing to reverse or reclass — the credit side would otherwise dangle.
        var payables = await db.SupplierPayables
            .Where(p => p.OrderId == msg.OrderId)
            .ToListAsync(context.CancellationToken);
        if (payables.Count == 0)
        {
            logger.LogInformation(
                "ReturnedGoodsValued: RMA {RmaId} order {OrderId} has no COGS accrual; skipping the {Kind} correction",
                msg.RmaId, msg.OrderId, msg.Kind);
            return;
        }

        var accruedNet = payables.Sum(p => p.NetPayableMinor);
        var accruedGross = payables.Sum(p => p.GrossMinor);
        if (accruedNet <= 0 || accruedGross <= 0)
        {
            return;
        }

        // The accrual booked the supplier's NET payable (gross less commission); the returned-goods value
        // (msg.CostMinor) is GROSS. Scale the net by the returned share of gross and CAP at the net
        // actually accrued, so a full return reverses exactly the net posted and a partial return reverses
        // its proportion — never more than was booked. Banker's rounding, as ExecuteRefundConsumer.
        var returnedGross = Math.Min(msg.CostMinor, accruedGross);
        var reversalMinor = (long)Math.Round((decimal)accruedNet * returnedGross / accruedGross, MidpointRounding.ToEven);
        if (reversalMinor <= 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        // Reverse the accrual in the currency it was booked in (Payments accrued in the order currency).
        var currency = payables[0].Currency;
        var cogsAccount = msg.StorefrontId is { } sid ? Accounts.CogsStoreFor(sid) : null;
        var writeoffAccount = msg.StorefrontId is { } wsid ? Accounts.WriteoffsStoreFor(wsid) : null;

        // Disposition edit (revision > 1): undo the previous revision's posting first, so the books reflect
        // only the current disposition. Append-only — a faithful line-swap reversal, never an edit.
        if (msg.Revision > 1)
        {
            var priorReference = $"{msg.RmaId}:{msg.Revision - 1}";
            var prior = await db.JournalEntries
                .Include(e => e.Lines)
                .FirstOrDefaultAsync(e => e.Reference == priorReference, context.CancellationToken);
            if (prior is not null)
            {
                db.JournalEntries.Add(Ledger.ReverseOf(prior, $"{reference}:reversal", now));
            }
        }

        var entry = msg.Kind == Restock
            ? Ledger.CogsReversal(msg.RmaId, msg.Revision, msg.OrderId, reversalMinor, currency, now, cogsAccount)
            : Ledger.Writeoff(msg.RmaId, msg.Revision, msg.OrderId, reversalMinor, currency, now, cogsAccount, writeoffAccount);

        db.JournalEntries.Add(entry);
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
