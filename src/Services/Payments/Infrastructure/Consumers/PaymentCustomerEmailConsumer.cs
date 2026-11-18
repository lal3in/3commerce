using MassTransit;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Records a backfilled account email on the member's vault (<c>payment_customer_email_backfill</c>) — see
/// <see cref="PaymentCustomerEmailBackfill"/>. Its queue, <c>payment-customer-email</c>, is derived from this type
/// (ADR-0060) and receives only the backfill tool's point-to-point sends. Idempotent: a vault that knows an email is never
/// changed, so a redelivery or a re-run does nothing. Logs ids and the outcome, never the email.
/// </summary>
public sealed class PaymentCustomerEmailConsumer(PaymentCustomerEmailBackfill backfill, ILogger<PaymentCustomerEmailConsumer> logger)
    : IConsumer<RecordPaymentCustomerEmail>
{
    public async Task Consume(ConsumeContext<RecordPaymentCustomerEmail> context)
    {
        var outcome = await backfill.RecordAsync(context.Message, context.CancellationToken);
        logger.LogInformation(
            "Payment customer email backfill for user {UserId} in tenant {TenantId}: {Outcome}",
            context.Message.UserId, context.Message.TenantId, outcome);
    }
}
