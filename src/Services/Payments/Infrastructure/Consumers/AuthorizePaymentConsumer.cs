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
                msg.OrderId,
                existing.AmountMinor,
                existing.Currency,
                msg.IdempotencyKey,
                existing.ProviderCustomerId,
                existing.ProviderPaymentMethodId,
                setupFutureUsage: false,
                context.CancellationToken);
            await context.RespondAsync(new AuthorizePaymentResult(
                existing.PaymentIntentId, existingIntent.ClientSecret, existing.AmountMinor, existing.TaxMinor));
            return;
        }

        var taxMinor = tax.TaxFor(msg.NetMinor, msg.Currency, msg.ShipCountry);
        var grossMinor = msg.NetMinor + taxMinor;
        var customer = msg.UserId is { } userId
            ? await db.PaymentCustomers.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == userId && c.Provider == "stripe", context.CancellationToken)
            : null;
        var savedMethod = msg.SavedPaymentMethodId is { } methodId
            ? await db.SavedPaymentMethods.AsNoTracking().SingleOrDefaultAsync(m => m.Id == methodId && m.UserId == msg.UserId && m.State == SavedPaymentMethodState.Active, context.CancellationToken)
            : null;
        var providerCustomerId = customer?.ProviderCustomerId;
        var providerPaymentMethodId = savedMethod?.ProviderPaymentMethodId;
        var intent = await provider.CreateIntentAsync(
            msg.OrderId,
            grossMinor,
            msg.Currency,
            msg.IdempotencyKey,
            providerCustomerId,
            providerPaymentMethodId,
            msg.SavePaymentMethod && providerCustomerId is not null,
            context.CancellationToken);

        db.Payments.Add(new Payment
        {
            Id = Guid.CreateVersion7(),
            OrderId = msg.OrderId,
            PaymentIntentId = intent.PaymentIntentId,
            AmountMinor = grossMinor,
            TaxMinor = taxMinor,
            Currency = msg.Currency,
            Status = PaymentStatus.Pending,
            ProviderCustomerId = providerCustomerId,
            ProviderPaymentMethodId = providerPaymentMethodId,
            SavePaymentMethodRequested = msg.SavePaymentMethod,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(context.CancellationToken);

        await context.RespondAsync(new AuthorizePaymentResult(intent.PaymentIntentId, intent.ClientSecret, grossMinor, taxMinor));
    }
}
