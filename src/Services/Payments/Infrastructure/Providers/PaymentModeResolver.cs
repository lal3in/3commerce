using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Providers;

/// <summary>
/// Reconciles the two notions of "mode" (ADR-0039). <see cref="PaymentProviderMode"/> (Test|Live) is
/// the per-tenant "which credential set" fact on the account; <see cref="PaymentMode"/>
/// (LocalMock|Sandbox|Production) is the RESOLVED runtime behavior. The <c>Payments:Mode</c> host
/// config is the ceiling (default LocalMock in Development, Production otherwise) and resolution
/// FAILS CLOSED toward Production safety:
/// <list type="bullet">
/// <item>Production host: only a Live account resolves; a Test account is refused; never the mock adapter.</item>
/// <item>Sandbox host: only a Test account resolves; a Live account is refused.</item>
/// <item>LocalMock host: always LocalMock (the mock adapter), overriding the declared provider — offline dev needs no credentials.</item>
/// </list>
/// </summary>
public sealed class PaymentModeResolver(IConfiguration configuration, IHostEnvironment environment)
{
    /// <summary>The host-level mode ceiling: <c>Payments:Mode</c>, defaulting to LocalMock in Development, Production otherwise.</summary>
    public PaymentMode ResolveHostMode()
    {
        var configured = configuration["Payments:Mode"];
        if (!string.IsNullOrWhiteSpace(configured) && Enum.TryParse<PaymentMode>(configured, ignoreCase: true, out var mode))
        {
            return mode;
        }

        return environment.IsDevelopment() ? PaymentMode.LocalMock : PaymentMode.Production;
    }

    /// <summary>
    /// Resolves the runtime <see cref="PaymentMode"/> for <paramref name="account"/>, failing closed
    /// on unsafe host×account combinations (throws <see cref="PaymentModeException"/>).
    /// </summary>
    public PaymentMode Resolve(PaymentAccountSnapshot account)
    {
        var host = ResolveHostMode();

        // Production host HARD-refuses anything but a Live account and NEVER the mock path.
        if (host == PaymentMode.Production)
        {
            return account.Mode == PaymentProviderMode.Live
                ? PaymentMode.Production
                : throw new PaymentModeException("Production host cannot run a Test payment account.");
        }

        if (host == PaymentMode.Sandbox)
        {
            return account.Mode == PaymentProviderMode.Test
                ? PaymentMode.Sandbox
                : throw new PaymentModeException("Sandbox host cannot run a Live payment account.");
        }

        return PaymentMode.LocalMock; // offline; no credentials required; declared provider overridden by the mock
    }

    /// <summary>
    /// A synthetic account for the host's default provider, used by call sites that do not yet carry
    /// per-order account context. The mode maps from the host ceiling (Production→Live, else Test) so
    /// <see cref="Resolve"/> never throws for the default path, exactly reproducing the old startup
    /// singleton selection (mock in LocalMock, else <c>Payments:DefaultProvider</c>, default stripe).
    /// </summary>
    public PaymentAccountSnapshot DefaultAccountForHost()
    {
        var host = ResolveHostMode();
        var provider = configuration["Payments:DefaultProvider"] is { Length: > 0 } p ? p : "stripe";
        var accountMode = host == PaymentMode.Production ? PaymentProviderMode.Live : PaymentProviderMode.Test;
        return new PaymentAccountSnapshot(Guid.Empty, Guid.Empty, null, provider, accountMode, null);
    }

    /// <summary>
    /// A synthetic account for a SPECIFIC settling provider (payment-method routing, ADR-0039), used
    /// when the shopper's chosen method maps to a standalone PSP (PayPal/Afterpay/Polar) rather than
    /// the tenant default. Mirrors <see cref="DefaultAccountForHost"/> exactly — same host→mode logic
    /// (Production→Live, else Test) so <see cref="Resolve"/> never throws for the routed path — but
    /// carries <paramref name="providerKey"/> instead of <c>Payments:DefaultProvider</c>. Dev has zero
    /// rows in <c>payments.PaymentAccounts</c>, so a synthetic snapshot keeps dev working with no
    /// seeding (in LocalMock the mock adapter settles regardless of the declared provider).
    /// </summary>
    public PaymentAccountSnapshot AccountForProvider(string providerKey)
    {
        var host = ResolveHostMode();
        var accountMode = host == PaymentMode.Production ? PaymentProviderMode.Live : PaymentProviderMode.Test;
        return new PaymentAccountSnapshot(Guid.Empty, Guid.Empty, null, providerKey, accountMode, null);
    }
}
