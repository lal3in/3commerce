using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Webhooks;

/// <summary>
/// HMAC-SHA256 signing for outbound webhooks (mt6_6). The receiver recomputes
/// <c>HMAC(secret, "{timestamp}.{payload}")</c> and compares — the timestamp binds the signature to a
/// moment so a captured body can't be replayed indefinitely. Compared in constant time.
/// </summary>
public static class WebhookSignature
{
    public static string Compute(string secret, long timestampUnix, string payload)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestampUnix}.{payload}"));
        return Convert.ToHexStringLower(mac);
    }

    public static bool Verify(string secret, long timestampUnix, string payload, string signatureHex)
    {
        var expected = Encoding.UTF8.GetBytes(Compute(secret, timestampUnix, payload));
        var provided = Encoding.UTF8.GetBytes(signatureHex.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}

/// <summary>
/// Validates a tenant-supplied webhook endpoint (mt6_6) — HTTPS only, and never a loopback/private/
/// link-local host. This is the anti-SSRF gate: a tenant must not be able to point a webhook at our
/// internal network.
/// </summary>
public static class WebhookEndpoint
{
    public static bool IsAllowed(string url, out string? error)
    {
        error = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "The webhook URL is not a valid absolute URL.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Webhook endpoints must use https.";
            return false;
        }

        if (IsPrivateOrLoopback(uri.Host))
        {
            error = "Webhook endpoints must be publicly routable (no loopback/private/link-local hosts).";
            return false;
        }

        return true;
    }

    private static bool IsPrivateOrLoopback(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            return false; // a DNS name — resolution-time SSRF is the delivery layer's concern
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        var b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            return b[0] == 10                                   // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254);                // 169.254.0.0/16 link-local
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b.Length == 16 && (b[0] & 0xfe) == 0xfc); // fc00::/7 ULA
    }
}
