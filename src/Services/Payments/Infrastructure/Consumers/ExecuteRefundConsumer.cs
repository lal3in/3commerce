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
/// <para>
/// Every rejection is ANNOUNCED as <see cref="RefundFailed"/>, never just logged: a requester (the RMA
/// saga) that hears nothing back has no terminal state to move to and strands in RefundPending forever
/// — which is how a refund could silently never happen (rma_disc).
/// </para>
/// </summary>
public sealed class ExecuteRefundConsumer(
    PaymentsDbContext db,
    IPaymentProviderRegistry registry,
    PaymentModeResolver modeResolver,
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

        // The order's checkout payment, or — when the request names one — a renewal / usage charge's own payment
        // (renewal_usage_refund_path). A named charge must belong to the order the request names, if it names one.
        var payment = string.IsNullOrWhiteSpace(msg.ChargeReference)
            ? await db.Payments.SingleOrDefaultAsync(p => p.OrderId == msg.OrderId && p.Kind == PaymentKind.Order, context.CancellationToken)
            : await db.Payments.SingleOrDefaultAsync(
                p => p.ChargeReference == msg.ChargeReference && (msg.OrderId == Guid.Empty || p.OrderId == msg.OrderId),
                context.CancellationToken);
        if (payment is null || payment.Status is PaymentStatus.Pending or PaymentStatus.Failed)
        {
            logger.LogWarning(
                "Refund {RefundId}: order {OrderId} / charge {ChargeReference} has no captured payment",
                msg.RefundId, msg.OrderId, msg.ChargeReference);
            await context.Publish(new RefundFailed(
                msg.RefundId, msg.OrderId, msg.AmountMinor, RefundFailureReason.NoCapturedPayment,
                msg.ChargeReference is { Length: > 0 }
                    ? $"Charge {msg.ChargeReference} has no captured payment to refund."
                    : "The order has no captured payment to refund."));
            return;
        }

        var remaining = payment.AmountMinor - payment.RefundedMinor;
        if (msg.AmountMinor <= 0 || msg.AmountMinor > remaining)
        {
            logger.LogWarning("Refund {RefundId}: amount {Amount} exceeds remaining {Remaining}", msg.RefundId, msg.AmountMinor, remaining);
            await context.Publish(new RefundFailed(
                msg.RefundId, msg.OrderId, msg.AmountMinor, RefundFailureReason.AmountExceedsRemaining,
                $"Requested {msg.AmountMinor} but only {remaining} of the payment is still refundable."));
            return;
        }

        // Refund through the PSP that actually settled the sale (payment.Provider), not the host
        // default — otherwise a PayPal/Polar/Afterpay sale is refunded down Stripe's rail while the
        // ledger credits cash.{provider}, so rail and books disagree (ADR-0039).
        var provider = RefundProviderResolver.ForPayment(registry, modeResolver, payment.Provider);
        var result = await provider.RefundAsync(payment.PaymentIntentId, msg.AmountMinor, msg.RefundId.ToString(), context.CancellationToken);
        if (!result.Succeeded)
        {
            logger.LogWarning("Refund {RefundId}: provider declined", msg.RefundId);
            await context.Publish(new RefundFailed(
                msg.RefundId, msg.OrderId, msg.AmountMinor, RefundFailureReason.ProviderDeclined,
                "The payment provider declined the refund."));
            return;
        }

        // Proportional tax + shipping reversal (banker's rounding), so full refunds return all of each. A renewal / usage
        // charge's TaxMinor is the tax its own journal entry booked (Payment.ForSettledCharge reads it from the entry), so
        // its reversal never re-derives tax from today's rate or the subscription's price.
        var taxPortion = payment.AmountMinor == 0 ? 0
            : (long)Math.Round((decimal)payment.TaxMinor * msg.AmountMinor / payment.AmountMinor, MidpointRounding.ToEven);
        var shippingPortion = payment.AmountMinor == 0 ? 0
            : (long)Math.Round((decimal)payment.ShippingMinor * msg.AmountMinor / payment.AmountMinor, MidpointRounding.ToEven);

        db.Refunds.Add(new Refund
        {
            Id = msg.RefundId,
            OrderId = payment.OrderId,
            PaymentIntentId = payment.PaymentIntentId,
            AmountMinor = msg.AmountMinor,
            Status = RefundStatus.Completed,
            CreatedAt = time.GetUtcNow(),
        });
        // Reverse into the storefront's own revenue/tax via its receivable bridge (phase 2c) when the
        // sale was storefront-attributed; otherwise the shared contra-revenue accounts.
        var accounts = payment.StorefrontId is { } sid
            ? await db.StorefrontLedgerAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.StorefrontId == sid, context.CancellationToken)
            : null;
        db.JournalEntries.Add(Ledger.Refund(
            msg.RefundId, payment.OrderId, msg.AmountMinor, taxPortion, payment.Currency, time.GetUtcNow(),
            payment.MethodKind, payment.Provider, accounts?.RevenueAccountCode, accounts?.TaxAccountCode, accounts?.ReceivableAccountCode,
            shippingPortion, accounts?.ShippingAccountCode, payment.StorefrontId,
            payment.Kind == PaymentKind.Order ? null : $"Refund {msg.RefundId} of charge {payment.ChargeReference} via {payment.MethodKind}"));

        payment.RefundedMinor += msg.AmountMinor;
        var fullyRefunded = payment.RefundedMinor >= payment.AmountMinor;
        if (fullyRefunded)
        {
            payment.Status = PaymentStatus.Refunded;
        }

        await db.SaveChangesAsync(context.CancellationToken);
        await context.Publish(new RefundCompleted(
            msg.RefundId, payment.OrderId, msg.AmountMinor, fullyRefunded, payment.ChargeReference));
    }
}
