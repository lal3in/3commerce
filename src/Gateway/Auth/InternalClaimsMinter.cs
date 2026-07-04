using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ThreeCommerce.Gateway.Auth;

/// <summary>
/// Mints the short-lived ES256 internal-claims JWT (ADR-0012). The gateway is the
/// ONLY holder of the private key; services verify with the public key.
/// </summary>
public sealed class InternalClaimsMinter
{
    public const string Issuer = "3commerce-gateway";
    public const string Audience = "3commerce-internal";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly SigningCredentials _credentials;
    private readonly JsonWebTokenHandler _handler = new();

    public InternalClaimsMinter(IConfiguration configuration, IHostEnvironment environment)
    {
        var pem = configuration["InternalAuth:PrivateKey"]
            ?? throw new InvalidOperationException("InternalAuth:PrivateKey is not configured.");

        // Launch gate (BL-11): never sign with the committed dev key outside Development. The
        // Gateway is deliberately reference-free, so the small fingerprint check lives inline.
        if (!environment.IsDevelopment() && KnownDevKeyFingerprint == Fingerprint(pem))
        {
            throw new InvalidOperationException(
                "Refusing to start with the committed development InternalAuth private key outside " +
                "Development. Rotate per-environment secrets (docs/ops/secrets.md) and supply them " +
                "via the InternalAuth__PrivateKey environment variable.");
        }

        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        _credentials = new SigningCredentials(new ECDsaSecurityKey(ecdsa), SecurityAlgorithms.EcdsaSha256);
    }

    public string Mint(Guid userId, string role, Guid sessionId, Guid tenantId, string? email = null,
        string? amr = null, DateTimeOffset? authTime = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(),
            ["role"] = role,
            ["sid"] = sessionId.ToString(),
            ["tenant"] = tenantId.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(email))
        {
            claims["email"] = email;
        }

        // MFA posture (mt6_10): amr "pwd"/"pwd otp"; auth_time = last strong verification, the
        // freshness anchor services compare against StepUp windows.
        if (!string.IsNullOrWhiteSpace(amr))
        {
            claims["amr"] = amr;
        }

        if (authTime is { } strongAuthAt)
        {
            claims["auth_time"] = strongAuthAt.ToUnixTimeSeconds();
        }

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            Expires = now + Lifetime,
            SigningCredentials = _credentials,
            Claims = claims,
        });
    }

    // SHA-256 of the committed dev private key's base64 body (not secret).
    private const string KnownDevKeyFingerprint = "ffe1e85a9de62f89169bb1ff40f687b1c7bc844d36006699ab60b4360d4ad35b";

    private static string Fingerprint(string pem)
    {
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
