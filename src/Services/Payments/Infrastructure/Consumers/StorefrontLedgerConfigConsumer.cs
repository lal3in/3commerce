using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Keeps Payments' local copy of each storefront's ledger account codes (phase 2, ADR-0008), fed by
/// Catalog's <see cref="StorefrontConfigChanged"/>, plus its tax rate + inclusiveness (ADR-0063 P4). Upsert per storefront; an event from an older
/// Catalog that carries no codes is ignored so it never blanks an existing projection.
/// </summary>
public sealed class StorefrontLedgerConfigConsumer(PaymentsDbContext db, TimeProvider time) : IConsumer<StorefrontConfigChanged>
{
    public async Task Consume(ConsumeContext<StorefrontConfigChanged> context)
    {
        var m = context.Message;
        if (string.IsNullOrWhiteSpace(m.RevenueAccountCode))
        {
            return; // pre-phase-2 Catalog; leave any existing row untouched
        }

        var row = await db.StorefrontLedgerAccounts.SingleOrDefaultAsync(x => x.StorefrontId == m.StorefrontId, context.CancellationToken);
        if (row is null)
        {
            db.StorefrontLedgerAccounts.Add(new StorefrontLedgerAccounts
            {
                StorefrontId = m.StorefrontId,
                ReceivableAccountCode = m.ReceivableAccountCode,
                RevenueAccountCode = m.RevenueAccountCode,
                TaxAccountCode = m.TaxAccountCode,
                ShippingAccountCode = string.IsNullOrWhiteSpace(m.ShippingAccountCode) ? null : m.ShippingAccountCode,
                TaxRateBasisPoints = m.TaxRateBasisPoints,
                TaxInclusive = m.TaxInclusive,
                UpdatedAt = time.GetUtcNow(),
            });
        }
        else
        {
            row.ReceivableAccountCode = m.ReceivableAccountCode;
            row.RevenueAccountCode = m.RevenueAccountCode;
            row.TaxAccountCode = m.TaxAccountCode;
            row.ShippingAccountCode = string.IsNullOrWhiteSpace(m.ShippingAccountCode) ? null : m.ShippingAccountCode;
            // The tax terms ride the same event (ADR-0063 P4): renewals and usage charges resolve the rate in
            // force at the charge from here, the way checkout resolves it from Ordering's StorefrontTaxCopy.
            row.TaxRateBasisPoints = m.TaxRateBasisPoints;
            row.TaxInclusive = m.TaxInclusive;
            row.UpdatedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
