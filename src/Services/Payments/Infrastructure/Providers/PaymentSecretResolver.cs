using Microsoft.Extensions.Configuration;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Providers;

/// <summary>
/// Per-provider, per-mode secret lookup (ADR-0039). Reads <c>"{Provider}:{key}"</c> from configuration
/// and asserts the value's prefix matches the resolved mode — a <c>sk_live_</c> key under a Sandbox
/// host (or a <c>sk_test_</c> key under Production) is refused with a typed
/// <see cref="PaymentConfigurationException"/> (fail-closed, mirroring DevSecretGuard's fingerprint
/// refusal). LocalMock needs no external credentials, so nothing is required or asserted there.
/// </summary>
public sealed class PaymentSecretResolver(IConfiguration configuration)
{
    // Known key prefixes per provider and mode. Providers without an entry skip prefix assertion
    // (PayPal/Polar/Afterpay credentials carry no test/live prefix — they are gated by base URL +
    // the mandatory presence check below, so a Sandbox/Production host still refuses to run without
    // its mode-appropriate credentials).
    private static readonly IReadOnlyDictionary<string, (string[] Sandbox, string[] Production)> Prefixes =
        new Dictionary<string, (string[], string[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["stripe"] = (["sk_test_", "rk_test_"], ["sk_live_", "rk_live_"]),
        };

    // Per-provider API base URLs (pay_4). Sandbox vs Production is the effective production gate for
    // the PSP adapters whose credentials carry no prefix: a Sandbox host always talks to the sandbox
    // host, a Production host to the live host — resolved from the account mode, never mixed.
    private static readonly IReadOnlyDictionary<string, (string Sandbox, string Production)> BaseUrls =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["polar"] = ("https://sandbox-api.polar.sh", "https://api.polar.sh"),
            ["paypal"] = ("https://api-m.sandbox.paypal.com", "https://api-m.paypal.com"),
            ["afterpay"] = ("https://global-api-sandbox.afterpay.com", "https://global-api.afterpay.com"),
        };

    /// <summary>
    /// Resolves the provider's API base URL for the resolved <paramref name="mode"/> (pay_4). Throws
    /// <see cref="PaymentConfigurationException"/> if the provider has no known endpoint. A per-provider
    /// <c>"{Provider}:BaseUrl"</c> config override wins so ops can pin an endpoint without a code change.
    /// </summary>
    public string BaseUrl(string provider, PaymentMode mode)
    {
        if (configuration[$"{provider}:BaseUrl"] is { Length: > 0 } overrideUrl)
        {
            return overrideUrl;
        }

        if (!BaseUrls.TryGetValue(provider.ToLowerInvariant(), out var urls))
        {
            throw new PaymentConfigurationException($"No API base URL is configured for provider '{provider}'.");
        }

        return mode == PaymentMode.Production ? urls.Production : urls.Sandbox;
    }

    /// <summary>
    /// Returns the configured secret for <paramref name="provider"/>/<paramref name="key"/>, asserting
    /// its prefix matches <paramref name="mode"/>. Throws <see cref="PaymentConfigurationException"/>
    /// if the value is missing (Sandbox/Production) or its prefix contradicts the mode.
    /// </summary>
    public string Get(string provider, PaymentMode mode, string key)
    {
        var value = configuration[$"{provider}:{key}"];

        if (mode == PaymentMode.LocalMock)
        {
            return value ?? string.Empty; // offline; no external credential required
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PaymentConfigurationException($"{provider}:{key} is not configured for {mode}.");
        }

        if (Prefixes.TryGetValue(provider, out var prefixes))
        {
            var (allowed, forbidden) = mode == PaymentMode.Production
                ? (prefixes.Production, prefixes.Sandbox)
                : (prefixes.Sandbox, prefixes.Production);

            if (forbidden.Any(p => value.StartsWith(p, StringComparison.Ordinal)))
            {
                throw new PaymentConfigurationException(
                    $"{provider}:{key} has a credential whose prefix does not match {mode}. " +
                    "Refusing to run a live key under a test host (or vice versa).");
            }

            // If the value carries a recognizable prefix at all, it must be an allowed one for the mode.
            var knownAny = prefixes.Sandbox.Concat(prefixes.Production).ToArray();
            if (knownAny.Any(p => value.StartsWith(p, StringComparison.Ordinal))
                && !allowed.Any(p => value.StartsWith(p, StringComparison.Ordinal)))
            {
                throw new PaymentConfigurationException(
                    $"{provider}:{key} prefix does not match {mode}.");
            }
        }

        return value;
    }
}
