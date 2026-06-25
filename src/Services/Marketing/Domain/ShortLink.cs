using System.Security.Cryptography;

namespace ThreeCommerce.Marketing.Domain;

public enum ShortLinkStatus { Active = 1, Disabled = 2 }

/// <summary>URL-safe base62 short codes (mt5_3).</summary>
public static class ShortCode
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static string Generate(int length = 7)
    {
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(chars);
    }

    public static bool IsValid(string? code) =>
        code is { Length: >= 4 and <= 16 } && code.All(Alphabet.Contains);
}

/// <summary>
/// Anti-open-redirect destination check (mt5_3 GOTCHA): a short link may only point at a REGISTERED
/// storefront domain over http/https — never an arbitrary external URL, so the redirector can't be
/// abused to launder traffic to a malicious site.
/// </summary>
public static class ShortLinkDestination
{
    public static bool IsAllowed(string url, IReadOnlySet<string> allowedHosts, out string? error)
    {
        error = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "The destination is not a valid absolute URL.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            error = "The destination must be http(s).";
            return false;
        }

        if (!allowedHosts.Contains(uri.Host.ToLowerInvariant()))
        {
            error = "The destination host is not a registered storefront domain.";
            return false;
        }

        return true;
    }
}

/// <summary>
/// A platform short link (mt5_3): a short code that redirects to a validated storefront destination,
/// optionally carrying a campaign <c>cid</c>. A click is recorded BEFORE the redirect; a disabled or
/// expired link stops redirecting.
/// </summary>
public sealed class ShortLink
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string Code { get; init; }
    public required string Destination { get; init; }
    public string? Cid { get; init; }
    public ShortLinkStatus Status { get; private set; } = ShortLinkStatus.Active;
    public DateTimeOffset? ExpiresAt { get; init; }
    public long ClickCount { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }

    private ShortLink() { }

    public static ShortLink Create(
        Guid tenantId, string code, string destination, IReadOnlySet<string> allowedHosts, DateTimeOffset now,
        string? cid = null, DateTimeOffset? expiresAt = null)
    {
        if (!ShortCode.IsValid(code))
        {
            throw new MarketingRuleException("A short code must be 4–16 url-safe characters.");
        }

        if (!ShortLinkDestination.IsAllowed(destination, allowedHosts, out var error))
        {
            throw new MarketingRuleException(error!);
        }

        return new ShortLink
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Code = code,
            Destination = destination,
            Cid = Campaign.SanitizeCid(cid),
            ExpiresAt = expiresAt,
            CreatedAt = now,
        };
    }

    public bool IsFollowable(DateTimeOffset now) =>
        Status == ShortLinkStatus.Active && (ExpiresAt is null || now < ExpiresAt);

    /// <summary>Record a click and return the destination — or null when disabled/expired (no redirect).</summary>
    public string? Follow(DateTimeOffset now)
    {
        if (!IsFollowable(now))
        {
            return null;
        }

        ClickCount++; // recorded before the redirect happens
        return Destination;
    }

    public void Disable() => Status = ShortLinkStatus.Disabled;
}
