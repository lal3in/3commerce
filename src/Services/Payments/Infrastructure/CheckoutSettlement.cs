using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>
/// Where a checkout's money settles (<c>AuthorizePaymentConsumer</c>, ADR-0039 routing + ADR-0042 per-storefront accounts):
/// the settling <see cref="PaymentAccountSnapshot"/> and the adapter the keyed registry resolves for it.
/// <para>
/// Both the account and the member's vault are looked up in the ORDER's tenant — <see cref="AuthorizePayment.TenantId"/>,
/// filled by Ordering's checkout — never the host's configured default tenant (ADR-0039 amendment 2026-11-26). With the host
/// default, a second tenant's order matched none of its own storefront's accounts and silently fell back to the synthetic
/// host-default account: the sale ran down the platform's default PSP and booked to <c>cash.store.{sf}.{that PSP}</c>, its
/// refund followed it, and the tenant's configured acquirer was never used. Only a message without a tenant (an in-flight
/// command from an Ordering that predates the field) still uses the host default, and that is logged.
/// </para>
/// </summary>
public static class CheckoutSettlement
{
    /// <summary>The seeded default tenant, used when <c>Tenancy:DefaultTenantId</c> is not configured.</summary>
    public static readonly Guid SeededDefaultTenantId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>The host's default tenant: config <c>Tenancy:DefaultTenantId</c>, else the seeded default tenant.</summary>
    public static Guid HostDefaultTenantId(IConfiguration configuration) =>
        Guid.TryParse(configuration["Tenancy:DefaultTenantId"], out var tenantId) ? tenantId : SeededDefaultTenantId;

    /// <summary>
    /// The tenant a checkout authorization runs in: the order's own (<see cref="AuthorizePayment.TenantId"/>). A message
    /// without one — sent by an Ordering that predates the field, still in flight across a deploy — falls back to the
    /// host's default tenant, with a warning, exactly as the vault read did before (#339).
    /// </summary>
    public static Guid TenantOf(AuthorizePayment message, IConfiguration configuration, ILogger logger)
    {
        if (message.TenantId is { } tenantId && tenantId != Guid.Empty)
        {
            return tenantId;
        }

        var fallback = HostDefaultTenantId(configuration);
        logger.LogWarning(
            "AuthorizePayment for order {OrderId} carries no tenant (a message from an older Ordering); resolving its payment account and vault in the host default tenant {TenantId}",
            message.OrderId, fallback);
        return fallback;
    }

    /// <summary>
    /// Selects the settling account + adapter for <paramref name="methodKind"/> in <paramref name="tenantId"/>'s
    /// <paramref name="storefrontId"/> (ADR-0039 routing), honouring the operator's configured <see cref="PaymentAccount"/>s
    /// (psp_acquirer_rma):
    /// <list type="bullet">
    /// <item>Card / Apple Pay / Google Pay (<see cref="PaymentMethodKindMapper.SettlingProviderFor"/> → null) are card-PSP
    /// methods, so the ACQUIRER is the storefront's active default account. A Polar default therefore makes card payments
    /// settle through Polar and post to cash.polar. With no configured default the synthetic
    /// <see cref="PaymentModeResolver.DefaultAccountForHost"/> (stripe) is kept.</item>
    /// <item>PayPal / Afterpay / Polar are standalone PSPs: prefer an active account whose provider matches the routed key,
    /// else the synthetic <see cref="PaymentModeResolver.AccountForProvider"/>.</item>
    /// </list>
    /// Every account goes through the registry, so the host×account mode gate (<see cref="PaymentModeResolver"/>) applies;
    /// an unsafe combination or a provider with no adapter degrades to the synthetic host default rather than crash
    /// checkout. In LocalMock the mock adapter is returned regardless of the declared provider, while the persisted provider
    /// stays the routed/acquiring PSP (ledger → cash.{provider}).
    /// </summary>
    public static async Task<(PaymentAccountSnapshot Account, IPaymentProvider Provider)> ResolveAsync(
        PaymentsDbContext db, IPaymentProviderRegistry registry, PaymentModeResolver modeResolver,
        Guid tenantId, PaymentMethodKind methodKind, Guid? storefrontId, CancellationToken ct)
    {
        // Payment accounts are per-storefront (ADR-0042) and tenant-owned: resolve within the order's tenant AND storefront.
        // With no storefront (defensive) or none configured, degrade to the synthetic host default below.
        if (storefrontId is { } sid)
        {
            if (PaymentMethodKindMapper.SettlingProviderFor(methodKind) is { Length: > 0 } routedKey)
            {
                // Standalone PSP: prefer a configured active account on that provider (a real snapshot), else the
                // synthetic account for the routed provider.
                var configured = await db.PaymentAccounts.AsNoTracking()
                    .Where(a => a.TenantId == tenantId && a.StorefrontId == sid && a.State == PaymentAccountState.Active && a.Provider.ToLower() == routedKey)
                    .OrderByDescending(a => a.IsDefaultForStorefront)
                    .FirstOrDefaultAsync(ct);
                var routed = configured is not null ? configured.ToSnapshot() : modeResolver.AccountForProvider(routedKey);
                if (TryResolve(registry, routed) is { } routedResult)
                {
                    return routedResult;
                }
            }
            else
            {
                // Card family: the acquirer is the storefront's default active account, if one is configured.
                var acquirer = await db.PaymentAccounts.AsNoTracking()
                    .Where(a => a.TenantId == tenantId && a.StorefrontId == sid && a.IsDefaultForStorefront && a.State == PaymentAccountState.Active)
                    .FirstOrDefaultAsync(ct);
                if (acquirer is not null && TryResolve(registry, acquirer.ToSnapshot()) is { } acquirerResult)
                {
                    return acquirerResult;
                }
            }
        }

        var fallback = modeResolver.DefaultAccountForHost();
        return (fallback, registry.Resolve(fallback));
    }

    /// <summary>Resolves the adapter for <paramref name="account"/>, or null when the host×account mode combination is
    /// unsafe / the provider has no adapter — the caller then degrades to the default.</summary>
    private static (PaymentAccountSnapshot Account, IPaymentProvider Provider)? TryResolve(
        IPaymentProviderRegistry registry, PaymentAccountSnapshot account)
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
}
