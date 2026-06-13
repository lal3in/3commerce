using ThreeCommerce.Identity.Infrastructure.Security;

namespace ThreeCommerce.Identity.Tests;

public class OpaqueTokenTests
{
    [Fact]
    public void Generated_tokens_are_unique_and_url_safe()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => OpaqueToken.Generate()).ToList();

        Assert.Equal(100, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.DoesNotContain(t, c => c is '+' or '/' or '='));
        // 256 bits base64url ≈ 43 chars
        Assert.All(tokens, t => Assert.True(t.Length >= 42));
    }

    [Fact]
    public void HashOf_is_deterministic_and_not_the_raw_token()
    {
        var raw = OpaqueToken.Generate();
        Assert.Equal(OpaqueToken.HashOf(raw), OpaqueToken.HashOf(raw));
        Assert.NotEqual(raw, OpaqueToken.HashOf(raw));
        Assert.Equal(64, OpaqueToken.HashOf(raw).Length); // SHA-256 hex
    }
}
