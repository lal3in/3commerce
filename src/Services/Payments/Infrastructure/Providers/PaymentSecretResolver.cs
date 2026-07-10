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
    // Known key prefixes per provider and mode. Providers without an entry skip prefix assertion.
    private static readonly IReadOnlyDictionary<string, (string[] Sandbox, string[] Production)> Prefixes =
        new Dictionary<string, (string[], string[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["stripe"] = (["sk_test_", "rk_test_"], ["sk_live_", "rk_live_"]),
        };

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
