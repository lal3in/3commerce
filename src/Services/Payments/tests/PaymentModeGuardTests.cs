using Microsoft.Extensions.Hosting;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// Boot-time Production-safety guard (ADR-0039, BL-11 sibling): a non-Development host refuses to
/// start when configured onto the mock/email path. Development is always allowed; a safe Production
/// config passes.
/// </summary>
public class PaymentModeGuardTests
{
    [Fact]
    public void Refuses_LocalMock_outside_development()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PaymentModeGuard.EnsureProductionSafe(
            PaymentTestSupport.Config(("Payments:Mode", "LocalMock")), PaymentTestSupport.Env(Environments.Production)));
        Assert.Contains("LocalMock", ex.Message);
    }

    [Fact]
    public void Refuses_AllowMockEmail_outside_development()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PaymentModeGuard.EnsureProductionSafe(
            PaymentTestSupport.Config(("Payments:Mode", "Sandbox"), ("Payments:AllowMockEmail", "true")),
            PaymentTestSupport.Env(Environments.Production)));
        Assert.Contains("AllowMockEmail", ex.Message);
    }

    [Fact]
    public void Allows_localmock_and_mock_email_in_development()
    {
        // Should not throw.
        PaymentModeGuard.EnsureProductionSafe(
            PaymentTestSupport.Config(("Payments:Mode", "LocalMock"), ("Payments:AllowMockEmail", "true")),
            PaymentTestSupport.Env(Environments.Development));
    }

    [Fact]
    public void Allows_a_safe_production_config()
    {
        // Sandbox host with no mock email, non-development → passes.
        PaymentModeGuard.EnsureProductionSafe(
            PaymentTestSupport.Config(("Payments:Mode", "Production")), PaymentTestSupport.Env(Environments.Production));
    }
}
