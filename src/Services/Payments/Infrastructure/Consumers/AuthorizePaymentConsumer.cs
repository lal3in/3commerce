using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Request/response: creates the payment intent for the order gross, persists a pending Payment,
/// and returns the client secret. Idempotent on OrderId so a retried request reuses the
/// existing intent rather than creating a second. Tax is owned by Ordering (storefront-configured,
/// projected via StorefrontConfigChanged) and is already inside NetMinor — Payments charges it
/// verbatim and must never apply a second tax on top.
/// <para>
/// Everything tenant-owned it reads — the storefront's settling payment account and the member's vault and card — is
/// read in the ORDER's tenant (<see cref="AuthorizePayment.TenantId"/>, <see cref="CheckoutSettlement.TenantOf"/>), never
/// the host's configured default tenant (ADR-0039 amendment 2026-11-26).
/// </para>
/// </summary>
public sealed class AuthorizePaymentConsumer(
    PaymentsDbContext db,
    IPaymentProviderRegistry registry,
    PaymentModeResolver modeResolver,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<AuthorizePaymentConsumer> logger) : IConsumer<AuthorizePayment>
{
    public async Task Consume(ConsumeContext<AuthorizePayment> context)
    {
        var msg = context.Message;
        var tenantId = CheckoutSettlement.TenantOf(msg, configuration, logger);
        // Map checkout's paymentOption → numeric PaymentMethodKind (ADR-0039), then route the method to
        // the PSP that settles it. Card/Apple Pay/Google Pay stay on the card PSP (the storefront's default
        // account); PayPal/Afterpay/Polar are standalone PSPs and settle on their own provider so the
        // ledger posts to cash.{provider}. Applies to both the create and the retry branch below, so a
        // shopper switching wallet on retry re-attributes correctly.
        var methodKind = PaymentMethodKindMapper.From(msg.PaymentOption);
        var (account, provider) = await CheckoutSettlement.ResolveAsync(
            db, registry, modeResolver, tenantId, methodKind, msg.StorefrontId, context.CancellationToken);
        var existing = await db.Payments.SingleOrDefaultAsync(p => p.OrderId == msg.OrderId && p.Kind == PaymentKind.Order, context.CancellationToken);
        if (existing is not null)
        {
            var existingIntent = await provider.AuthorizeAsync(
                new PaymentRequest(
                    msg.OrderId,
                    existing.AmountMinor,
                    existing.Currency,
                    msg.IdempotencyKey,
                    methodKind,
                    account,
                    existing.ProviderCustomerId,
                    existing.ProviderPaymentMethodId),
                context.CancellationToken);

            // A retried checkout may carry a different paymentOption (the shopper switched wallet):
            // keep the persisted method in step so the ledger attributes what was actually used.
            var settlingProvider = LedgerProviders.Normalize(account.Provider);
            if (existing.MethodKind != methodKind || existing.Provider != settlingProvider)
            {
                existing.MethodKind = methodKind;
                existing.Provider = settlingProvider;
                await db.SaveChangesAsync(context.CancellationToken);
            }

            await context.RespondAsync(new AuthorizePaymentResult(
                existing.PaymentIntentId, existingIntent.ClientSecret ?? string.Empty, existing.AmountMinor, existing.TaxMinor));
            return;
        }

        var grossMinor = msg.NetMinor;
        // PaymentCustomers and SavedPaymentMethods are under FORCE RLS (ADR-0024, owner decisions 2026-11-07 + 2026-11-15),
        // so the member's vault and card are read in the ORDER's tenant, carried on the message — never derived from the
        // tables themselves (a scope-less read of them returns nothing).
        var instrument = await CheckoutInstrument.FindAsync(
            db, tenantId, msg.UserId, msg.SavedPaymentMethodId, context.CancellationToken);
        var providerCustomerId = instrument.ProviderCustomerId;
        var providerPaymentMethodId = instrument.ProviderPaymentMethodId;
        var intent = await provider.AuthorizeAsync(
            new PaymentRequest(
                msg.OrderId,
                grossMinor,
                msg.Currency,
                msg.IdempotencyKey,
                methodKind,
                account,
                providerCustomerId,
                providerPaymentMethodId,
                SetupFutureUsage: msg.SavePaymentMethod && providerCustomerId is not null),
            context.CancellationToken);

        db.Payments.Add(new Payment
        {
            Id = Guid.CreateVersion7(),
            OrderId = msg.OrderId,
            StorefrontId = msg.StorefrontId, // per-storefront ledger attribution (phase 2)
            PaymentIntentId = intent.PaymentIntentId,
            AmountMinor = grossMinor,
            TaxMinor = msg.TaxMinor, // tax portion of the charge → ledger books it as a tax liability (not revenue)
            ShippingMinor = msg.ShippingMinor, // shipping portion → ledger books it as its own income line
            // ADR-0063 § 16: the OSS part of the tax by destination → each posted to its own OSS liability account.
            OssTax = OssTaxSplit.Of(msg.OssTax?.Select(d => ((string?)d.Country, d.TaxMinor))).Within(msg.TaxMinor).Serialize(),
            Currency = msg.Currency,
            Status = PaymentStatus.Pending,
            // The shopper's chosen method and the settling PSP, both persisted so the ledger can
            // attribute the sale (cash.{provider}, "via {MethodKind}") instead of assuming Stripe.
            MethodKind = methodKind,
            Provider = LedgerProviders.Normalize(account.Provider),
            ProviderCustomerId = providerCustomerId,
            ProviderPaymentMethodId = providerPaymentMethodId,
            SavePaymentMethodRequested = msg.SavePaymentMethod,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(context.CancellationToken);

        await context.RespondAsync(new AuthorizePaymentResult(intent.PaymentIntentId, intent.ClientSecret ?? string.Empty, grossMinor, msg.TaxMinor));
    }
}
