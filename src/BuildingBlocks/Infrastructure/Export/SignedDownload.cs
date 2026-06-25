using ThreeCommerce.BuildingBlocks.Infrastructure.Webhooks;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Export;

/// <summary>
/// Expiring signed download links for async exports (mt6_8). A token is <c>{expiryUnix}.{hmac}</c> over
/// the resource id + expiry — it grants access to exactly one artifact until it expires, with no
/// server-side session. Reuses the webhook HMAC (constant-time verify). Sensitive exports are also
/// audited (mt6_2) and the artifact itself lives in object storage (mt6_9).
/// </summary>
public static class SignedDownload
{
    public static string CreateToken(string secret, string resourceId, DateTimeOffset expiresAt)
    {
        var expiry = expiresAt.ToUnixTimeSeconds();
        return $"{expiry}.{WebhookSignature.Compute(secret, expiry, resourceId)}";
    }

    public static bool IsValid(string secret, string resourceId, string token, DateTimeOffset now)
    {
        var separator = token.IndexOf('.');
        if (separator <= 0 || !long.TryParse(token.AsSpan(0, separator), out var expiry))
        {
            return false;
        }

        if (now.ToUnixTimeSeconds() >= expiry)
        {
            return false; // expired
        }

        return WebhookSignature.Verify(secret, expiry, resourceId, token[(separator + 1)..]);
    }
}
