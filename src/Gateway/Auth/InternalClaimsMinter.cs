using System.Security.Cryptography;
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

    public InternalClaimsMinter(IConfiguration configuration)
    {
        var pem = configuration["InternalAuth:PrivateKey"]
            ?? throw new InvalidOperationException("InternalAuth:PrivateKey is not configured.");
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        _credentials = new SigningCredentials(new ECDsaSecurityKey(ecdsa), SecurityAlgorithms.EcdsaSha256);
    }

    public string Mint(Guid userId, string role, Guid sessionId)
    {
        var now = DateTime.UtcNow;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            Expires = now + Lifetime,
            SigningCredentials = _credentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId.ToString(),
                ["role"] = role,
                ["sid"] = sessionId.ToString(),
            },
        });
    }
}
