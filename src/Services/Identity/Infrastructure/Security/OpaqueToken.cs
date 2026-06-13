using System.Security.Cryptography;
using System.Text;

namespace ThreeCommerce.Identity.Infrastructure.Security;

/// <summary>
/// Opaque tokens: 256-bit CSPRNG, base64url on the wire, only the SHA-256 stored.
/// </summary>
public static class OpaqueToken
{
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string HashOf(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
