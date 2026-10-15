using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Shared posting for a settled metered charge (pay_usage_charges_post_revenue): books the charge as a
/// receivable-bridged <see cref="Ledger.Sale"/> attributed to the balance's storefront (revenue.store-{id}
/// / cash.store-{id}.{provider}) — the same shape PaymentEventProcessor posts for an order sale. Reused by
/// the arrears overage and prepaid auto-load consumers so both record revenue identically. The caller has
/// already de-duped on <paramref name="reference"/>; this owns the SaveChanges. No fee/shipping.
/// <para>
/// Tax (ADR-0063 P4, renewal_usage_tax_2026_10_11 — before this a metered charge posted tax 0): the unit price
/// is a storefront shelf price (ADR-0038), so the storefront's CURRENT regime + rate apply, split exactly as
/// checkout splits it (<see cref="RecurringChargeTax"/>). Inclusive: the contained tax moves from revenue to
/// <c>tax.store-{id}</c>, the gross charged is unchanged. Exclusive: no tax posted, pending the owner's decision.
/// </para>
/// </summary>
internal static class UsageRevenuePoster
{
    public static async Task PostAsync(
        PaymentsDbContext db, TimeProvider time, ILogger logger, Guid? storefrontId, long chargeMinor, string currency,
        string provider, string reference, string description, CancellationToken ct)
    {
        var accounts = storefrontId is { } sid
            ? await db.StorefrontLedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.StorefrontId == sid, ct)
            : null;
        var tax = RecurringChargeTax.For(chargeMinor, accounts?.TaxRateBasisPoints, accounts?.TaxInclusive ?? false);
        SubscriptionService.LogUnpostedTax(logger, tax.Basis, reference, storefrontId);
        var sale = Ledger.Sale(
            Guid.Empty, chargeMinor, tax.TaxMinor, 0, currency, time.GetUtcNow(),
            PaymentMethodKind.Card, provider, accounts?.RevenueAccountCode, accounts?.TaxAccountCode, accounts?.ReceivableAccountCode,
            0, accounts?.ShippingAccountCode, storefrontId, reference, description);
        if (sale.Lines.Count > 0)
        {
            db.JournalEntries.Add(sale);
            await db.SaveChangesAsync(ct);
        }
    }
}
