using System.Security.Cryptography;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

namespace ThreeCommerce.Identity.Tests;

/// <summary>
/// BL-11 launch gate: the committed dev signing key must never run outside Development.
/// </summary>
public class DevSecretGuardTests
{
    // The public half of the committed dev keypair (Identity/Api/appsettings.json).
    private const string CommittedDevPublicKey =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIqGAVsNMjocmiuWYRiz3EOKEtFQ4\n" +
        "BLWLuMFiwwNjKxo1YWjtGCHQRKI30dIMi8NjEx8XgWNGHXlq22QfCuSABQ==\n" +
        "-----END PUBLIC KEY-----";

    [Fact]
    public void Dev_key_is_recognised()
    {
        Assert.True(DevSecretGuard.IsKnownDevKey(CommittedDevPublicKey));
    }

    [Fact]
    public void Production_with_dev_key_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DevSecretGuard.EnsureProductionKey(CommittedDevPublicKey, isDevelopment: false));
        Assert.Contains("docs/ops/secrets.md", ex.Message);
    }

    [Fact]
    public void Development_with_dev_key_is_allowed()
    {
        // Must not throw — committed keys exist precisely for zero-friction local dev.
        DevSecretGuard.EnsureProductionKey(CommittedDevPublicKey, isDevelopment: true);
    }

    [Fact]
    public void Production_with_a_rotated_key_is_allowed()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var freshPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        Assert.False(DevSecretGuard.IsKnownDevKey(freshPem));
        DevSecretGuard.EnsureProductionKey(freshPem, isDevelopment: false); // no throw
    }
}
