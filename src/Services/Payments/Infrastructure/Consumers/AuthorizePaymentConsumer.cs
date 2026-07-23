using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
/// </summary>
public sealed class AuthorizePaymentConsumer(
    PaymentsDbContext db,
    IPaymentProviderRegistry registry,
    PaymentModeResolver modeResolver,
    IConfiguration configuration,
    TimeProvider time) : IConsumer<AuthorizePayment>
{
    private static readonly Guid FallbackTenantId = new("00000000-0000-0000-0000-000000000001");

    public async Task Consume(ConsumeContext<AuthorizePayment> context)
    {
        var msg = context.Message;
        // Map checkout's paymentOption → numeric PaymentMethodKind (ADR-0039), then route the method to
        // the PSP that settles it. Card/Apple Pay/Google Pay stay on the card PSP (the tenant default
        // account); PayPal/Afterpay/Polar are standalone PSPs and settle on their own provider so the
        // ledger posts to cash.{provider}. Applies to both the create and the retry branch below, so a
        // shopper switching wallet on retry re-attributes correctly.
        var methodKind = PaymentMethodKindMapper.From(msg.PaymentOption);
        var (account, provider) = await ResolveSettlementAsync(methodKind, context.CancellationToken);
        var existing = await db.Payments.SingleOrDefaultAsync(p => p.OrderId == msg.OrderId, context.CancellationToken);
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
            PaymentIntentId = intent.PaymentIntentId,
            AmountMinor = grossMinor,
            TaxMinor = 0, // tax lives on the Ordering attempt/order, not the payment
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

        await context.RespondAsync(new AuthorizePaymentResult(intent.PaymentIntentId, intent.ClientSecret ?? string.Empty, grossMinor, TaxMinor: 0));
    }

    /// <summary>
    /// Selects the settling account + adapter for <paramref name="methodKind"/> (ADR-0039 routing),
    /// now honouring the operator's configured tenant <see cref="PaymentAccount"/>s (psp_acquirer_rma):
    /// <list type="bullet">
    /// <item>Card / Apple Pay / Google Pay (<see cref="PaymentMethodKindMapper.SettlingProviderFor"/> →
    /// null) are card-PSP methods, so the ACQUIRER is whichever active account is the tenant default. A
    /// Polar default therefore makes card payments settle through Polar and post to cash.polar. With no
    /// configured default we keep the synthetic <see cref="PaymentModeResolver.DefaultAccountForHost"/>
    /// (stripe) fallback exactly.</item>
    /// <item>PayPal / Afterpay / Polar are standalone PSPs: prefer an active account whose provider
    /// matches the routed key, else the synthetic <see cref="PaymentModeResolver.AccountForProvider"/>
    /// (the prior behavior).</item>
    /// </list>
    /// The account lookup is tenant-scoped: <see cref="AuthorizePayment"/> carries no tenant in dev, so
    /// we resolve it exactly as the rest of Payments does — config <c>Tenancy:DefaultTenantId</c>, else
    /// the seeded default tenant. Every DB-backed resolution keeps the try/catch degradation to the
    /// synthetic default so an unsafe host×account mode combination can never crash checkout; in
    /// LocalMock the mock adapter is returned regardless of the declared provider, while the persisted
    /// provider becomes the routed/acquiring PSP (ledger → cash.{provider}).
    /// </summary>
    private async Task<(PaymentAccountSnapshot Account, IPaymentProvider Provider)> ResolveSettlementAsync(
        PaymentMethodKind methodKind, CancellationToken ct)
    {
        var tenantId = DefaultTenantId();

        if (PaymentMethodKindMapper.SettlingProviderFor(methodKind) is { Length: > 0 } routedKey)
        {
            // Standalone PSP: prefer a configured active account on that provider (a real snapshot),
            // else the synthetic account for the routed provider.
            var configured = await db.PaymentAccounts.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.State == PaymentAccountState.Active && a.Provider.ToLower() == routedKey)
                .OrderByDescending(a => a.IsDefaultForTenant)
                .FirstOrDefaultAsync(ct);
            var routed = configured is not null ? SnapshotOf(configured) : modeResolver.AccountForProvider(routedKey);
            if (TryResolve(routed) is { } routedResult)
            {
                return routedResult;
            }
        }
        else
        {
            // Card family: the acquirer is the tenant's default active account, if one is configured.
            var acquirer = await db.PaymentAccounts.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.IsDefaultForTenant && a.State == PaymentAccountState.Active)
                .FirstOrDefaultAsync(ct);
            if (acquirer is not null && TryResolve(SnapshotOf(acquirer)) is { } acquirerResult)
            {
                return acquirerResult;
            }
        }

        var fallback = modeResolver.DefaultAccountForHost();
        return (fallback, registry.Resolve(fallback));
    }

    /// <summary>Resolves the adapter for <paramref name="account"/>, or null when the host×account mode
    /// combination is unsafe / the provider has no adapter — the caller then degrades to the default.</summary>
    private (PaymentAccountSnapshot Account, IPaymentProvider Provider)? TryResolve(PaymentAccountSnapshot account)
    {
        try
        {
            return (account, registry.Resolve(account));
        }
        catch (Exception ex) when (ex is PaymentConfigurationException or PaymentModeException)
        {
            return null;
        }
    }

    private static PaymentAccountSnapshot SnapshotOf(PaymentAccount account) =>
        new(account.Id, account.TenantId, account.StorefrontId, account.Provider, account.Mode, account.ExternalAccountRef);

    private Guid DefaultTenantId() =>
        Guid.TryParse(configuration["Tenancy:DefaultTenantId"], out var tenantId) ? tenantId : FallbackTenantId;
}
