using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Books the carrier-label cost accrual (phase 1) when Fulfillment buys a label: Dr the
/// storefront's own shipping-cost expense (or the shared fallback when the order isn't
/// storefront-attributed) / Cr liability.carrier_payable. No real money moves. Idempotent by
/// PackageId (the journal reference), so a re-bought label for the same package posts only once.
/// </summary>
public sealed class ShippingLabelPurchasedConsumer(
    PaymentsDbContext db, TimeProvider time, ILogger<ShippingLabelPurchasedConsumer> logger) : IConsumer<ShippingLabelPurchased>
{
    public async Task Consume(ConsumeContext<ShippingLabelPurchased> context)
    {
        var msg = context.Message;
        if (msg.CostMinor <= 0)
        {
            logger.LogWarning(
                "ShippingLabelPurchased for package {PackageId}: non-positive cost {CostMinor}, skipping",
                msg.PackageId, msg.CostMinor);
            return;
        }

        if (await db.JournalEntries.AnyAsync(e => e.Reference == msg.PackageId.ToString(), context.CancellationToken))
        {
            return; // already booked
        }

        var payment = await db.Payments.SingleOrDefaultAsync(p => p.OrderId == msg.OrderId, context.CancellationToken);
        var storeAccount = payment?.StorefrontId is { } sid ? Accounts.ShippingCostStoreFor(sid) : null;

        // Post in the payment's settlement currency when known, NOT the carrier's cost currency
        // (msg.Currency is always AUD from the fake carrier) — otherwise a EUR/USD store's carrier
        // cost lands in AUD and Financials' by-storefront Margin column (which sums account amounts
        // across currencies) subtracts AUD minor units from EUR revenue. Absent an FX feed this
        // treats the AUD-derived minor amount as store-currency minor units without conversion — the
        // same pragmatic posture as the mock PSP settling into cash.stripe; FX conversion is
        // explicitly out of scope (plan NOTES).
        var currency = payment?.Currency ?? msg.Currency;

        db.JournalEntries.Add(Ledger.CarrierCost(msg.PackageId, msg.OrderId, msg.CostMinor, currency, time.GetUtcNow(), storeAccount));
        await db.SaveChangesAsync(context.CancellationToken);
    }
}
