using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Request/response: creates the payment intent for the order gross, persists a pending Payment,
/// and returns the client secret. Idempotent on OrderId so a retried request reuses the
/// existing intent rather than creating a second. Tax is owned by Ordering (storefront-configured,
/// projected via StorefrontConfigChanged) and is already inside NetMinor — Payments charges it
/// verbatim and must never apply a second tax on top.
/// </summary>
public sealed class AuthorizePaymentConsumer(
    PaymentsDbContext db,
    IPaymentProviderRegistry registry,
    PaymentModeResolver modeResolver,
    TimeProvider time) : IConsumer<AuthorizePayment>
{
    public async Task Consume(ConsumeContext<AuthorizePayment> context)
    {
        var msg = context.Message;
        var account = modeResolver.DefaultAccountForHost();
        var provider = registry.Resolve(account);
        var existing = await db.Payments.SingleOrDefaultAsync(p => p.OrderId == msg.OrderId, context.CancellationToken);
        if (existing is not null)
        {
            var existingIntent = await provider.AuthorizeAsync(
                new PaymentRequest(
                    msg.OrderId,
                    existing.AmountMinor,
                    existing.Currency,
                    msg.IdempotencyKey,
                    PaymentMethodKind.Card,
                    account,
                    existing.ProviderCustomerId,
                    existing.ProviderPaymentMethodId),
                context.CancellationToken);
            await context.RespondAsync(new AuthorizePaymentResult(
                existing.PaymentIntentId, existingIntent.ClientSecret ?? string.Empty, existing.AmountMinor, existing.TaxMinor));
            return;
        }

        var grossMinor = msg.NetMinor;
        var customer = msg.UserId is { } userId
            ? await db.PaymentCustomers.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == userId && c.Provider == "stripe", context.CancellationToken)
            : null;
        var savedMethod = msg.SavedPaymentMethodId is { } methodId
            ? await db.SavedPaymentMethods.AsNoTracking().SingleOrDefaultAsync(m => m.Id == methodId && m.UserId == msg.UserId && m.State == SavedPaymentMethodState.Active, context.CancellationToken)
            : null;
        var providerCustomerId = customer?.ProviderCustomerId;
        var providerPaymentMethodId = savedMethod?.ProviderPaymentMethodId;
        var intent = await provider.AuthorizeAsync(
            new PaymentRequest(
                msg.OrderId,
                grossMinor,
                msg.Currency,
                msg.IdempotencyKey,
                PaymentMethodKind.Card,
                account,
                providerCustomerId,
                providerPaymentMethodId,
                SetupFutureUsage: msg.SavePaymentMethod && providerCustomerId is not null),
            context.CancellationToken);

        db.Payments.Add(new Payment
        {
            Id = Guid.CreateVersion7(),
            OrderId = msg.OrderId,
            PaymentIntentId = intent.PaymentIntentId,
            AmountMinor = grossMinor,
            TaxMinor = 0, // tax lives on the Ordering attempt/order, not the payment
            Currency = msg.Currency,
            Status = PaymentStatus.Pending,
            ProviderCustomerId = providerCustomerId,
            ProviderPaymentMethodId = providerPaymentMethodId,
            SavePaymentMethodRequested = msg.SavePaymentMethod,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(context.CancellationToken);

        await context.RespondAsync(new AuthorizePaymentResult(intent.PaymentIntentId, intent.ClientSecret ?? string.Empty, grossMinor, TaxMinor: 0));
    }
}
