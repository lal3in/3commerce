using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Infrastructure.Providers;

/// <summary>
/// Resolves the PSP adapter that must EXECUTE a refund: the same provider that settled the sale — the
/// payment's stored <c>Provider</c> — so a PayPal sale refunds down PayPal's rail instead of the host
/// default (ADR-0039). Without this a PayPal/Polar/Afterpay payment was refunded through Stripe while
/// the ledger still (correctly) credited <c>cash.{provider}</c>, so the rail and the books disagreed.
/// The provider is resolved through a synthetic account so the SAME mode gate as authorize applies
/// (the mock adapter in LocalMock; fail-closed on unsafe host×account combinations). It degrades to the
/// host default only when the stored provider has no adapter or the host×mode combination is refused,
/// so a refund can never crash on odd/legacy data — the ledger still attributes to the stored provider.
/// </summary>
public static class RefundProviderResolver
{
    public static IPaymentProvider ForPayment(
        IPaymentProviderRegistry registry, PaymentModeResolver modeResolver, string? storedProvider)
    {
        try
        {
            var account = modeResolver.AccountForProvider(LedgerProviders.Normalize(storedProvider));
            return registry.Resolve(account);
        }
        catch (Exception ex) when (ex is PaymentConfigurationException or PaymentModeException)
        {
            return registry.ResolveDefault();
        }
    }
}
