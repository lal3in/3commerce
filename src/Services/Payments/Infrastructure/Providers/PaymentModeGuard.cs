using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ThreeCommerce.Payments.Infrastructure.Providers;

/// <summary>
/// Boot-time Production-safety guard (ADR-0039), a sibling of <c>DevSecretGuard</c> (BL-11). The
/// TEST-only mock-payload email path must be IMPOSSIBLE to reach in Production, so rather than rely
/// on a runtime branch being taken, a mis-configured non-Development host REFUSES TO BOOT: it may
/// not be explicitly configured <c>Payments:Mode=LocalMock</c>, nor carry <c>Payments:AllowMockEmail=true</c>.
/// (An absent <c>Payments:Mode</c> in a non-Development host defaults to Production — safe.)
/// </summary>
public static class PaymentModeGuard
{
    public static void EnsureProductionSafe(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        var mode = configuration["Payments:Mode"];
        if (string.Equals(mode, "LocalMock", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to start: Payments:Mode=LocalMock is not allowed outside Development. " +
                "The mock-payment path (and its TEST-only payload email) must be unreachable in Production. " +
                "Set Payments:Mode=Sandbox or Production (docs/adr/0039-payment-provider-architecture.md).");
        }

        if (configuration.GetValue("Payments:AllowMockEmail", false))
        {
            throw new InvalidOperationException(
                "Refusing to start: Payments:AllowMockEmail=true is not allowed outside Development. " +
                "The TEST-only mock-payment payload email must never be emitted in Production " +
                "(docs/adr/0039-payment-provider-architecture.md).");
        }
    }
}
