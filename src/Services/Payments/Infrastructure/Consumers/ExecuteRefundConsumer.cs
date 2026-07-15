using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// The single refund execution path (ADR-0014): ledger reversal + provider refund +
/// RefundCompleted. Idempotent on RefundId. Rejects refunds exceeding the remaining balance.
/// </summary>
public sealed class ExecuteRefundConsumer(
    PaymentsDbContext db,
    IPaymentProviderRegistry registry,
    TimeProvider time,
    ILogger<ExecuteRefundConsumer> logger) : IConsumer<RefundRequested>
{
    public async Task Consume(ConsumeContext<RefundRequested> context)
    {
        var msg = context.Message;
        if (await db.Refunds.AnyAsync(r => r.Id == msg.RefundId, context.CancellationToken))
        {
            return; // already executed
        }

        var payment = await db.Payments.SingleOrDefaultAsync(p => p.OrderId == msg.OrderId, context.CancellationToken);
        if (payment is null || payment.Status is PaymentStatus.Pending or PaymentStatus.Failed)
        {
            logger.LogWarning("Refund {RefundId}: order {OrderId} has no captured payment", msg.RefundId, msg.OrderId);
            return;
        }

        var remaining = payment.AmountMinor - payment.RefundedMinor;
        if (msg.AmountMinor <= 0 || msg.AmountMinor > remaining)
        {
            logger.LogWarning("Refund {RefundId}: amount {Amount} exceeds remaining {Remaining}", msg.RefundId, msg.AmountMinor, remaining);
            return;
        }

        var provider = registry.ResolveDefault();
        var result = await provider.RefundAsync(payment.PaymentIntentId, msg.AmountMinor, msg.RefundId.ToString(), context.CancellationToken);
        if (!result.Succeeded)
        {
            logger.LogWarning("Refund {RefundId}: provider declined", msg.RefundId);
            return;
        }

        // Proportional tax reversal (banker's rounding), so full refunds return all tax.
        var taxPortion = payment.AmountMinor == 0 ? 0
            : (long)Math.Round((decimal)payment.TaxMinor * msg.AmountMinor / payment.AmountMinor, MidpointRounding.ToEven);

        db.Refunds.Add(new Refund
        {
            Id = msg.RefundId,
            OrderId = msg.OrderId,
            PaymentIntentId = payment.PaymentIntentId,
            AmountMinor = msg.AmountMinor,
            Status = RefundStatus.Completed,
            CreatedAt = time.GetUtcNow(),
        });
        db.JournalEntries.Add(Ledger.Refund(msg.RefundId, msg.OrderId, msg.AmountMinor, taxPortion, payment.Currency, time.GetUtcNow()));

        payment.RefundedMinor += msg.AmountMinor;
        var fullyRefunded = payment.RefundedMinor >= payment.AmountMinor;
        if (fullyRefunded)
        {
            payment.Status = PaymentStatus.Refunded;
        }

        await db.SaveChangesAsync(context.CancellationToken);
        await context.Publish(new RefundCompleted(msg.RefundId, msg.OrderId, msg.AmountMinor, fullyRefunded));
    }
}
