using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Request/response: prices tax, creates the payment intent, persists a pending Payment,
/// and returns the client secret. Idempotent on OrderId so a retried request reuses the
/// existing intent rather than creating a second.
/// </summary>
public sealed class AuthorizePaymentConsumer(
    PaymentsDbContext db,
    IPaymentProvider provider,
    ITaxStrategy tax,
    TimeProvider time) : IConsumer<AuthorizePayment>
{
    public async Task Consume(ConsumeContext<AuthorizePayment> context)
    {
        var msg = context.Message;
        var existing = await db.Payments.SingleOrDefaultAsync(p => p.OrderId == msg.OrderId, context.CancellationToken);
        if (existing is not null)
        {
            var existingIntent = await provider.CreateIntentAsync(
                msg.OrderId, existing.AmountMinor, existing.Currency, msg.IdempotencyKey, context.CancellationToken);
            await context.RespondAsync(new AuthorizePaymentResult(
                existing.PaymentIntentId, existingIntent.ClientSecret, existing.AmountMinor, existing.TaxMinor));
            return;
        }

        var taxMinor = tax.TaxFor(msg.NetMinor, msg.Currency);
        var grossMinor = msg.NetMinor + taxMinor;
        var intent = await provider.CreateIntentAsync(msg.OrderId, grossMinor, msg.Currency, msg.IdempotencyKey, context.CancellationToken);

        db.Payments.Add(new Payment
        {
            Id = Guid.CreateVersion7(),
            OrderId = msg.OrderId,
            PaymentIntentId = intent.PaymentIntentId,
            AmountMinor = grossMinor,
            TaxMinor = taxMinor,
            Currency = msg.Currency,
            Status = PaymentStatus.Pending,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(context.CancellationToken);

        await context.RespondAsync(new AuthorizePaymentResult(intent.PaymentIntentId, intent.ClientSecret, grossMinor, taxMinor));
    }
}
