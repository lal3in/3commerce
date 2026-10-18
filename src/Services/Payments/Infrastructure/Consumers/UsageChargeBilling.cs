using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Shared charge + posting for a metered usage charge — the arrears overage (<see cref="UsageOverageChargeConsumer"/>)
/// and the prepaid auto-load (<see cref="UsageAutoLoadChargeConsumer"/>) bill identically (pay_usage_charges_post_revenue):
/// <list type="number">
/// <item>de-dupe on <c>reference</c> (the intent key) — the same charge never double-charges or double-posts;</item>
/// <item>resolve the tax from the balance storefront's CURRENT regime + rate (the unit price is a storefront shelf
/// price, ADR-0038) with checkout's arithmetic (<see cref="RecurringChargeTax"/>, ADR-0063 P4);</item>
/// <item>charge the rail <see cref="RecurringChargeTaxResult.ChargeMinor"/> — the amount itself on an inclusive /
/// 0% / unknown-rate store, the amount + the added tax on an exclusive (US) store (owner decision 2026-10-15,
/// option A) — pinned to the reference on the first attempt (<see cref="RecurringChargePin"/>), so every retry
/// sends the same amount under the same idempotency key;</item>
/// <item>on a settled charge, post a receivable-bridged <see cref="Ledger.Sale"/> attributed to the storefront:
/// net to <c>revenue.store-{id}</c>, tax to <c>tax.store-{id}</c>, gross to <c>cash.store-{id}.{provider}</c>.
/// No fee/shipping.</item>
/// </list>
/// A usage charge has no ship-to, so it is never destination-exempt.
/// </summary>
public static class UsageChargeBilling
{
    public static async Task ChargeAndPostAsync(
        PaymentsDbContext db, IPaymentProviderRegistry registry, PaymentModeResolver modeResolver, TimeProvider time, ILogger logger,
        Guid? storefrontId, long amountMinor, string currency, string reference, string description, CancellationToken ct)
    {
        if (await db.JournalEntries.AnyAsync(e => e.Reference == reference, ct))
        {
            return; // already charged + posted
        }

        var accounts = storefrontId is { } sid
            ? await db.StorefrontLedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.StorefrontId == sid, ct)
            : null;
        // Computed once per reference and PINNED (persisted before the provider call): a redelivery of the same
        // message — even after the store's rate changed — re-sends the same amount under the same key, so the
        // provider de-duplicates it instead of rejecting a changed amount or charging twice.
        var tax = await RecurringChargePin.PinAsync(db, time, reference, () =>
        {
            var taxInclusive = accounts?.TaxInclusive ?? false;
            var computed = RecurringChargeTax.For(amountMinor, accounts?.TaxRateBasisPoints, taxInclusive);
            SubscriptionService.LogUnknownRate(logger, computed, taxInclusive, reference, storefrontId);
            return computed;
        }, ct);

        var account = modeResolver.DefaultAccountForHost();
        var response = await registry.Resolve(account).AuthorizeAsync(
            new PaymentRequest(Guid.Empty, tax.ChargeMinor, currency, reference, PaymentMethodKind.Card, account), ct);
        if (response.Outcome != PaymentOutcome.Succeeded)
        {
            return;
        }

        var sale = Ledger.Sale(
            Guid.Empty, tax.ChargeMinor, tax.TaxMinor, 0, currency, time.GetUtcNow(),
            PaymentMethodKind.Card, account.Provider, accounts?.RevenueAccountCode, accounts?.TaxAccountCode, accounts?.ReceivableAccountCode,
            0, accounts?.ShippingAccountCode, storefrontId, reference, description);
        if (sale.Lines.Count > 0)
        {
            db.JournalEntries.Add(sale);
            await db.SaveChangesAsync(ct);
        }
    }
}
