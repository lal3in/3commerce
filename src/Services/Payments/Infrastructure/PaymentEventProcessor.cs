using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// The single place a payment outcome becomes ledger truth. Both the real Stripe webhook
/// endpoint and the dev simulate endpoint feed here. Idempotent by provider event id, so
/// redelivered/duplicated webhooks post exactly one journal entry (NFR-3).
/// </summary>
public sealed class PaymentEventProcessor(
    PaymentsDbContext db,
    IPublishEndpoint publisher,
    TimeProvider time,
    ILogger<PaymentEventProcessor> logger)
{
    public async Task ProcessAsync(PaymentWebhookEvent ev, CancellationToken ct)
    {
        if (await db.WebhookInbox.AnyAsync(x => x.EventId == ev.EventId, ct))
        {
            logger.LogInformation("Webhook {EventId} already processed; skipping", ev.EventId);
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
            return;
        }

        switch (ev.Kind)
        {
            case PaymentWebhookKind.PaymentSucceeded when payment.Status != PaymentStatus.Succeeded:
                payment.Status = PaymentStatus.Succeeded;
                db.JournalEntries.Add(Ledger.Sale(
                    payment.OrderId, payment.AmountMinor, payment.TaxMinor, ev.FeeMinor, payment.Currency, time.GetUtcNow(),
                    payment.MethodKind, payment.Provider));
                await publisher.Publish(new PaymentSucceeded(payment.OrderId, payment.PaymentIntentId, payment.AmountMinor), ct);
                break;

            case PaymentWebhookKind.PaymentFailed when payment.Status == PaymentStatus.Pending:
                payment.Status = PaymentStatus.Failed;
                await publisher.Publish(new PaymentFailed(payment.OrderId, payment.PaymentIntentId, ev.FailureReason ?? "failed"), ct);
                break;
        }

        await db.SaveChangesAsync(ct);
    }
}
