using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.Identity.Tests;

public class TotpTests
{
    // RFC 6238 Appendix B, SHA-1 rows: ASCII secret "12345678901234567890" (Base32 below), 8 digits.
    private const string RfcSecretBase32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1111111111, "14050471")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    [InlineData(20000000000, "65353130")]
    public void Matches_the_rfc6238_test_vectors(long unixSeconds, string expected)
    {
        var code = Totp.Compute(RfcSecretBase32, DateTimeOffset.FromUnixTimeSeconds(unixSeconds), digits: 8);
        Assert.Equal(expected, code);
    }

    [Fact]
    public void Verify_accepts_current_and_adjacent_steps_only()
    {
        var secret = Totp.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        Assert.True(Totp.Verify(secret, Totp.Compute(secret, now), now));
        Assert.True(Totp.Verify(secret, Totp.Compute(secret, now.AddSeconds(-Totp.StepSeconds)), now));
        Assert.True(Totp.Verify(secret, Totp.Compute(secret, now.AddSeconds(Totp.StepSeconds)), now));
        Assert.False(Totp.Verify(secret, Totp.Compute(secret, now.AddSeconds(2 * Totp.StepSeconds)), now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("not-a-code")]
    public void Verify_rejects_malformed_codes_without_throwing(string code)
    {
        Assert.False(Totp.Verify(Totp.GenerateSecret(), code, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Verify_rejects_a_garbage_secret_without_throwing()
    {
        Assert.False(Totp.Verify("0189!!", "123456", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Generated_secrets_are_unique_base32_and_authenticator_compatible()
    {
        var secrets = Enumerable.Range(0, 50).Select(_ => Totp.GenerateSecret()).ToList();

        Assert.Equal(50, secrets.Distinct().Count());
        // 20 bytes -> 32 Base32 chars, alphabet-only (what authenticator manual entry expects).
        Assert.All(secrets, s => Assert.Equal(32, s.Length));
        Assert.All(secrets, s => Assert.All(s, c => Assert.Contains(c, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567")));
    }

    [Fact]
    public void Otpauth_uri_carries_the_standard_totp_parameters()
    {
        var uri = Totp.OtpauthUri("3commerce", "op@example.com", RfcSecretBase32);

        Assert.StartsWith("otpauth://totp/3commerce:op%40example.com?", uri);
        Assert.Contains($"secret={RfcSecretBase32}", uri);
        Assert.Contains("algorithm=SHA1", uri);
        Assert.Contains("digits=6", uri);
        Assert.Contains("period=30", uri);
    }
}
