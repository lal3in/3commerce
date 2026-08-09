using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Infrastructure.Redis;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// The single place a payment outcome becomes ledger truth. Both the real Stripe webhook
/// endpoint and the dev simulate endpoint feed here. Idempotent by provider event id, so
/// redelivered/duplicated webhooks post exactly one journal entry (NFR-3).
///
/// A Redis dedupe fast-path (ADR-0044) drops already-processed duplicate deliveries before they touch
/// Postgres. It is a positive cache populated only after the WebhookInbox row commits, so the Postgres
/// WebhookInbox remains the exactly-once guarantee — a Redis outage or evicted key just falls through to it.
/// </summary>
public sealed class PaymentEventProcessor(
    PaymentsDbContext db,
    IPublishEndpoint publisher,
    IDedupeStore dedupe,
    TimeProvider time,
    ILogger<PaymentEventProcessor> logger)
{
    // Covers the providers' redelivery window; Postgres backstops anything older, so this only bounds
    // how long the fast-path drops duplicates without a Postgres read.
    private static readonly TimeSpan DedupeTtl = TimeSpan.FromDays(3);

    public async Task ProcessAsync(PaymentWebhookEvent ev, CancellationToken ct)
    {
        var dedupeKey = "webhook:" + ev.EventId;

        // Fast-path: a confirmed, already-processed duplicate never touches Postgres.
        if (await dedupe.IsProcessedAsync(dedupeKey, ct))
        {
            RedisMetrics.RecordIdempotencyDedupe("hit");
            logger.LogInformation("Webhook {EventId} already processed (Redis fast-path); skipping", ev.EventId);
            return;
        }

        RedisMetrics.RecordIdempotencyDedupe("miss");

        // Durable guard (source of truth): the WebhookInbox still dedupes exactly-once even when Redis is
        // cold/unavailable or a key was evicted.
        if (await db.WebhookInbox.AnyAsync(x => x.EventId == ev.EventId, ct))
        {
            logger.LogInformation("Webhook {EventId} already processed; skipping", ev.EventId);
            await dedupe.MarkProcessedAsync(dedupeKey, DedupeTtl, ct); // re-prime the fast-path
            return;
        }

        db.WebhookInbox.Add(new WebhookInboxEntry { EventId = ev.EventId, ReceivedAt = time.GetUtcNow() });

        var payment = await db.Payments.SingleOrDefaultAsync(p => p.PaymentIntentId == ev.PaymentIntentId, ct);
        if (payment is null)
        {
            // Webhook arrived before AuthorizePayment persisted — record the inbox entry and
            // bail; the event will not be reprocessed, but for fake/dev this never happens and
            // for Stripe the intent is always created by us first. Logged for visibility.
            logger.LogWarning("No payment for intent {Intent}; webhook {EventId} recorded only", ev.PaymentIntentId, ev.EventId);
            await db.SaveChangesAsync(ct);
            await dedupe.MarkProcessedAsync(dedupeKey, DedupeTtl, ct);
            return;
        }

        switch (ev.Kind)
        {
            case PaymentWebhookKind.PaymentSucceeded when payment.Status != PaymentStatus.Succeeded:
                payment.Status = PaymentStatus.Succeeded;
                // Route revenue/tax to the storefront's own accounts when we know the storefront and
                // have its projected codes (phase 2); otherwise the shared revenue.sales / tax accounts.
                var accounts = payment.StorefrontId is { } sid
                    ? await db.StorefrontLedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.StorefrontId == sid, ct)
                    : null;
                var sale = Ledger.Sale(
                    payment.OrderId, payment.AmountMinor, payment.TaxMinor, ev.FeeMinor, payment.Currency, time.GetUtcNow(),
                    payment.MethodKind, payment.Provider, accounts?.RevenueAccountCode, accounts?.TaxAccountCode, accounts?.ReceivableAccountCode,
                    payment.ShippingMinor, accounts?.ShippingAccountCode);
                // A genuinely $0 order (e.g. a usage-metered product with no upfront price and no shipping)
                // moves no money, so the sale entry has no lines — don't persist an empty journal entry.
                if (sale.Lines.Count > 0)
                {
                    db.JournalEntries.Add(sale);
                }

                await publisher.Publish(new PaymentSucceeded(payment.OrderId, payment.PaymentIntentId, payment.AmountMinor), ct);
                break;

            case PaymentWebhookKind.PaymentFailed when payment.Status == PaymentStatus.Pending:
                payment.Status = PaymentStatus.Failed;
                await publisher.Publish(new PaymentFailed(payment.OrderId, payment.PaymentIntentId, ev.FailureReason ?? "failed"), ct);
                break;

            case PaymentWebhookKind.ChargebackOpened when payment.Status == PaymentStatus.Succeeded:
                // Reverse only what's still standing — a partial refund may already have clawed some back.
                var disputedGross = payment.AmountMinor - payment.RefundedMinor;
                if (disputedGross <= 0)
                {
                    // Already fully refunded before the dispute landed: nothing left to reverse. Record the
                    // inbox entry (done above) so the webhook isn't reprocessed, but post no journal entry.
                    logger.LogWarning("Chargeback for order {OrderId}: nothing un-refunded remains; recorded only", payment.OrderId);
                    break;
                }

                var cbAccounts = payment.StorefrontId is { } cbSid
                    ? await db.StorefrontLedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.StorefrontId == cbSid, ct)
                    : null;
                // Proportional tax + shipping slice of the disputed gross (banker's rounding, as refunds).
                var cbTax = payment.AmountMinor == 0 ? 0
                    : (long)Math.Round((decimal)payment.TaxMinor * disputedGross / payment.AmountMinor, MidpointRounding.ToEven);
                var cbShipping = payment.AmountMinor == 0 ? 0
                    : (long)Math.Round((decimal)payment.ShippingMinor * disputedGross / payment.AmountMinor, MidpointRounding.ToEven);
                db.JournalEntries.Add(Ledger.Chargeback(
                    payment.OrderId, disputedGross, cbTax, ev.FeeMinor, payment.Currency, time.GetUtcNow(),
                    payment.MethodKind, payment.Provider, cbAccounts?.RevenueAccountCode, cbAccounts?.TaxAccountCode, cbAccounts?.ReceivableAccountCode,
                    cbShipping, cbAccounts?.ShippingAccountCode));
                payment.Status = PaymentStatus.Disputed;
                await publisher.Publish(new PaymentDisputed(payment.OrderId, payment.PaymentIntentId, disputedGross), ct);
                break;
        }

        await db.SaveChangesAsync(ct);
        // Prime the fast-path only after the WebhookInbox row is durably committed, so Redis never claims
        // an event is processed that Postgres didn't record.
        await dedupe.MarkProcessedAsync(dedupeKey, DedupeTtl, ct);
    }
}
