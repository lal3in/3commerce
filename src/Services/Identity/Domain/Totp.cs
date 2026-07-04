using System.Security.Cryptography;

namespace ThreeCommerce.Identity.Domain;

/// <summary>
/// RFC 6238 TOTP over RFC 4226 HOTP (HMAC-SHA1, 30s step, 6 digits) — the profile every
/// authenticator app ships. Implemented in-domain so no OTP package enters the supply chain;
/// correctness is pinned to the RFC 6238 Appendix B test vectors in TotpTests.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>160-bit secret (RFC 4226 §4 recommended minimum), Base32 for authenticator-app entry.</summary>
    public static string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    /// <summary>Standard enrollment URI accepted by authenticator apps (manual-entry friendly).</summary>
    public static string OtpauthUri(string issuer, string account, string secretBase32) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={secretBase32}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    /// <summary>
    /// Accepts the current step ± <paramref name="window"/> steps (clock skew tolerance).
    /// Fixed-time comparison; malformed input is simply false — never an exception.
    /// </summary>
    public static bool Verify(string secretBase32, string code, DateTimeOffset now, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != Digits || TryBase32Decode(secretBase32) is not { } key)
        {
            return false;
        }

        var step = now.ToUnixTimeSeconds() / StepSeconds;
        var match = false;
        for (var offset = -window; offset <= window; offset++)
        {
            // Evaluate every candidate (no early exit) to keep verification time input-independent.
            var candidate = Compute(key, step + offset, Digits);
            match |= CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(candidate), System.Text.Encoding.ASCII.GetBytes(code));
        }

        return match;
    }

    /// <summary>Current code for a secret — used by enrollment confirm flows and tests.</summary>
    public static string Compute(string secretBase32, DateTimeOffset at, int digits = Digits) =>
        TryBase32Decode(secretBase32) is { } key
            ? Compute(key, at.ToUnixTimeSeconds() / StepSeconds, digits)
            : throw new ArgumentException("Invalid Base32 secret.", nameof(secretBase32));

    internal static string Compute(byte[] key, long counter, int digits)
    {
        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, message, hash);

        // RFC 4226 §5.3 dynamic truncation.
        var dtOffset = hash[19] & 0x0F;
        var binary = ((hash[dtOffset] & 0x7F) << 24) | (hash[dtOffset + 1] << 16) | (hash[dtOffset + 2] << 8) | hash[dtOffset + 3];
        return (binary % (int)Math.Pow(10, digits)).ToString().PadLeft(digits, '0');
    }

    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var result = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result.Append(Base32Alphabet[(buffer >> bits) & 0x1F]);
            }
        }

        if (bits > 0)
        {
            result.Append(Base32Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return result.ToString();
    }

    private static byte[]? TryBase32Decode(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        var trimmed = encoded.TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(trimmed.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in trimmed)
        {
            var index = Base32Alphabet.IndexOf(c);
            if (index < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return bytes.Count > 0 ? [.. bytes] : null;
    }
}
