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
                    payment.ShippingMinor, accounts?.ShippingAccountCode, payment.StorefrontId);
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

            // payment_intent.canceled: the authorization was voided before it settled. Only a still-pending
            // payment can be voided; a captured one goes through the dispute/refund paths instead.
            case PaymentWebhookKind.PaymentVoided when payment.Status == PaymentStatus.Pending:
                payment.Status = PaymentStatus.Voided;
                await publisher.Publish(new PaymentFailed(payment.OrderId, payment.PaymentIntentId, ev.FailureReason ?? "voided"), ct);
                break;

            // A dispute opened / funds were withdrawn. ChargebackOpened is the legacy alias. All three book
            // the reversal exactly once (idempotent on the "{orderId}:chargeback" reference) and flag Disputed.
            case PaymentWebhookKind.DisputeCreated:
                await EnsureDisputeReversedAsync(payment, ev, DisputeStatus.Created, ct);
                break;
            case PaymentWebhookKind.ChargebackOpened:
            case PaymentWebhookKind.DisputeFundsWithdrawn:
                await EnsureDisputeReversedAsync(payment, ev, DisputeStatus.FundsWithdrawn, ct);
                break;

            // A status-only update (e.g. under review) — track the sub-status, move no money.
            case PaymentWebhookKind.DisputeUpdated:
                payment.DisputeStatus = DisputeStatus.UnderReview;
                if (ev.ProviderDisputeId is { Length: > 0 } upDp) payment.ProviderDisputeId = upDp;
                break;

            // Funds reinstated / dispute won: reverse the chargeback reversal so the sale stands again.
            case PaymentWebhookKind.DisputeFundsReinstated:
                await ReinstateDisputeAsync(payment, DisputeStatus.FundsReinstated, ct);
                break;
            case PaymentWebhookKind.DisputeClosedWon:
                await ReinstateDisputeAsync(payment, DisputeStatus.Won, ct);
                break;

            // Dispute lost — the merchant lost the case as the final outcome: the payment becomes a
            // chargeback and a void payment record is created. The ledger reversal was booked on withdrawal
            // (ensured here in case we never saw that event).
            case PaymentWebhookKind.DisputeClosedLost:
                await LoseDisputeAsync(payment, ev, ct);
                break;
        }

        await db.SaveChangesAsync(ct);
        // Prime the fast-path only after the WebhookInbox row is durably committed, so Redis never claims
        // an event is processed that Postgres didn't record.
        await dedupe.MarkProcessedAsync(dedupeKey, DedupeTtl, ct);
    }

    /// <summary>
    /// Books the dispute reversal exactly once (idempotent on the <c>{orderId}:chargeback</c> reference) and
    /// flips the payment to <see cref="PaymentStatus.Disputed"/> with the given sub-status. Safe to call from
    /// created / funds-withdrawn / (legacy) chargeback-opened events and from the lost path — only the first
    /// call posts the journal entry and publishes <see cref="PaymentDisputed"/>.
    /// </summary>
    private async Task EnsureDisputeReversedAsync(Payment payment, PaymentWebhookEvent ev, DisputeStatus subStatus, CancellationToken ct)
    {
        payment.DisputeStatus = subStatus;
        if (ev.ProviderDisputeId is { Length: > 0 } dp) payment.ProviderDisputeId = dp;

        var reference = $"{payment.OrderId}:chargeback";
        var alreadyReversed = await db.JournalEntries.AnyAsync(e => e.Reference == reference, ct);

        // Only a captured (or already-disputed) payment has anything to reverse.
        if (payment.Status is not (PaymentStatus.Succeeded or PaymentStatus.Disputed))
        {
            logger.LogWarning("Dispute for order {OrderId} on a {Status} payment; sub-status only", payment.OrderId, payment.Status);
            return;
        }

        if (alreadyReversed)
        {
            payment.Status = PaymentStatus.Disputed; // idempotent: reversal already booked
            return;
        }

        // Reverse only what's still standing — a partial refund may already have clawed some back.
        var disputedGross = payment.AmountMinor - payment.RefundedMinor;
        if (disputedGross <= 0)
        {
            logger.LogWarning("Dispute for order {OrderId}: nothing un-refunded remains; status only", payment.OrderId);
            payment.Status = PaymentStatus.Disputed;
            return;
        }

        var accounts = payment.StorefrontId is { } sid
            ? await db.StorefrontLedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.StorefrontId == sid, ct)
            : null;
        // Proportional tax + shipping slice of the disputed gross (banker's rounding, as refunds).
        var tax = payment.AmountMinor == 0 ? 0
            : (long)Math.Round((decimal)payment.TaxMinor * disputedGross / payment.AmountMinor, MidpointRounding.ToEven);
        var shipping = payment.AmountMinor == 0 ? 0
            : (long)Math.Round((decimal)payment.ShippingMinor * disputedGross / payment.AmountMinor, MidpointRounding.ToEven);
        db.JournalEntries.Add(Ledger.Chargeback(
            payment.OrderId, disputedGross, tax, ev.FeeMinor, payment.Currency, time.GetUtcNow(),
            payment.MethodKind, payment.Provider, accounts?.RevenueAccountCode, accounts?.TaxAccountCode, accounts?.ReceivableAccountCode,
            shipping, accounts?.ShippingAccountCode, payment.StorefrontId));
        payment.Status = PaymentStatus.Disputed;
        await publisher.Publish(new PaymentDisputed(payment.OrderId, payment.PaymentIntentId, disputedGross), ct);
    }

    /// <summary>
    /// The merchant won / funds were reinstated: reverse the earlier chargeback entry (idempotent on the
    /// <c>{orderId}:chargeback-reinstate</c> reference) so the sale stands again, and restore the payment to
    /// <see cref="PaymentStatus.Succeeded"/>.
    /// </summary>
    private async Task ReinstateDisputeAsync(Payment payment, DisputeStatus subStatus, CancellationToken ct)
    {
        payment.DisputeStatus = subStatus;

        var chargebackRef = $"{payment.OrderId}:chargeback";
        var reinstateRef = $"{payment.OrderId}:chargeback-reinstate";
        var chargeback = await db.JournalEntries.Include(e => e.Lines).SingleOrDefaultAsync(e => e.Reference == chargebackRef, ct);
        var alreadyReinstated = await db.JournalEntries.AnyAsync(e => e.Reference == reinstateRef, ct);
        if (chargeback is not null && !alreadyReinstated)
        {
            db.JournalEntries.Add(Ledger.ReverseOf(chargeback, reinstateRef, time.GetUtcNow()));
        }

        // Funds are back with the merchant → the payment stands again (unless already terminally lost).
        if (payment.Status == PaymentStatus.Disputed)
        {
            payment.Status = PaymentStatus.Succeeded;
        }
    }

    /// <summary>
    /// The dispute closed as lost — the terminal chargeback outcome: ensure the reversal is booked, move the
    /// payment to <see cref="PaymentStatus.Chargeback"/>, create the void payment record (idempotent per
    /// original payment), and publish <see cref="PaymentChargedBack"/>.
    /// </summary>
    private async Task LoseDisputeAsync(Payment payment, PaymentWebhookEvent ev, CancellationToken ct)
    {
        await EnsureDisputeReversedAsync(payment, ev, DisputeStatus.Lost, ct);
        payment.DisputeStatus = DisputeStatus.Lost;
        payment.Status = PaymentStatus.Chargeback;

        var chargedBack = payment.AmountMinor - payment.RefundedMinor;
        if (!await db.VoidPayments.AnyAsync(v => v.OriginalPaymentId == payment.Id, ct))
        {
            db.VoidPayments.Add(new VoidPayment
            {
                Id = Guid.CreateVersion7(),
                OriginalPaymentId = payment.Id,
                OrderId = payment.OrderId,
                PaymentIntentId = payment.PaymentIntentId,
                ProviderDisputeId = ev.ProviderDisputeId ?? payment.ProviderDisputeId,
                AmountMinor = chargedBack,
                Currency = payment.Currency,
                Reason = "dispute_lost",
                CreatedAt = time.GetUtcNow(),
            });
        }

        await publisher.Publish(new PaymentChargedBack(payment.OrderId, payment.PaymentIntentId, chargedBack), ct);
    }
}
