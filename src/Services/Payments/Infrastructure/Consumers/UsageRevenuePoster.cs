using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Shared posting for a settled metered charge (pay_usage_charges_post_revenue): books the charge as a
/// receivable-bridged <see cref="Ledger.Sale"/> attributed to the balance's storefront (revenue.store-{id}
/// / cash.store-{id}.{provider}) — the same shape PaymentEventProcessor posts for an order sale. Reused by
/// the arrears overage and prepaid auto-load consumers so both record revenue identically. The caller has
/// already de-duped on <paramref name="reference"/>; this owns the SaveChanges. Metered charges carry no
/// tax/fee/shipping (the charge amount is treated as net revenue).
/// </summary>
internal static class UsageRevenuePoster
{
    public static async Task PostAsync(
        PaymentsDbContext db, TimeProvider time, Guid? storefrontId, long chargeMinor, string currency,
        string provider, string reference, string description, CancellationToken ct)
    {
        var accounts = storefrontId is { } sid
            ? await db.StorefrontLedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.StorefrontId == sid, ct)
            : null;
        var sale = Ledger.Sale(
            Guid.Empty, chargeMinor, 0, 0, currency, time.GetUtcNow(),
            PaymentMethodKind.Card, provider, accounts?.RevenueAccountCode, accounts?.TaxAccountCode, accounts?.ReceivableAccountCode,
            0, accounts?.ShippingAccountCode, storefrontId, reference, description);
        if (sale.Lines.Count > 0)
        {
            db.JournalEntries.Add(sale);
            await db.SaveChangesAsync(ct);
        }
    }
}
