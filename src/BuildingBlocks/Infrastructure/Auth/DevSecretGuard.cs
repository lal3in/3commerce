using System.Security.Cryptography;
using System.Text;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

/// <summary>
/// Launch gate (BL-11): the ES256 internal-claims keypair is committed for zero-friction
/// local dev, so anyone with the repo could mint admin tokens. This refuses to boot any
/// non-Development environment that is still configured with the committed dev keys,
/// forcing per-environment rotation (see docs/ops/secrets.md). Fingerprints are SHA-256
/// of the base64 key body — not secret, safe to ship — so the check needs no key material.
/// </summary>
public static class DevSecretGuard
{
    private static readonly HashSet<string> KnownDevKeyFingerprints =
    [
        "ffe1e85a9de62f89169bb1ff40f687b1c7bc844d36006699ab60b4360d4ad35b", // dev private key
        "a26efbb1361f633f9264b92ce587cd4820f0106bb2099ee47d864dc99bc00643", // dev public key
    ];

    /// <summary>
    /// Throws in non-Development environments when <paramref name="pem"/> is a known dev key.
    /// </summary>
    public static void EnsureProductionKey(string pem, bool isDevelopment)
    {
        if (isDevelopment || !IsKnownDevKey(pem))
        {
            return;
        }

        throw new InvalidOperationException(
            "Refusing to start with the committed development InternalAuth key outside Development. " +
            "Rotate per-environment secrets (docs/ops/secrets.md) and supply them via " +
            "InternalAuth__PrivateKey / InternalAuth__PublicKey environment variables.");
    }

    public static bool IsKnownDevKey(string pem) =>
        KnownDevKeyFingerprints.Contains(Fingerprint(pem));

    private static string Fingerprint(string pem)
    {
        // Hash only the base64 body so header/whitespace/escaping differences don't matter.
        var body = new StringBuilder();
        foreach (var line in pem.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("-----"))
            {
                body.Append(trimmed);
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(body.ToString())));
    }
}
