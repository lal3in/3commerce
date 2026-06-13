using ThreeCommerce.Identity.Infrastructure.Security;

namespace ThreeCommerce.Identity.Tests;

public class Argon2idPasswordHasherTests
{
    private readonly Argon2idPasswordHasher _hasher = new();

    [Fact]
    public void Verify_accepts_correct_password()
    {
        var hash = _hasher.Hash("correct horse battery staple");
        Assert.True(_hasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_rejects_wrong_password()
    {
        var hash = _hasher.Hash("correct horse battery staple");
        Assert.False(_hasher.Verify("Correct Horse Battery Staple", hash));
    }

    [Fact]
    public void Hash_is_salted_so_same_password_differs()
    {
        Assert.NotEqual(_hasher.Hash("same-password"), _hasher.Hash("same-password"));
    }

    [Fact]
    public void Hash_is_self_describing_argon2id()
    {
        var hash = _hasher.Hash("x");
        Assert.StartsWith("argon2id$m=19456,t=2,p=1$", hash);
    }

    [Fact]
    public void Verify_returns_false_for_malformed_hash()
    {
        Assert.False(_hasher.Verify("x", "not-a-valid-hash"));
    }
}
