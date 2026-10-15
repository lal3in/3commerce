using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ThreeCommerce.Workers.Notifications.Email;

/// <summary>
/// Boot-time safety guard for outgoing email, a sibling of Payments' <c>PaymentModeGuard</c> and the BL-11
/// <c>DevSecretGuard</c>. Today the only real sender is <see cref="LoggingEmailSender"/>: nothing is delivered,
/// emails only reach the log. Outside Development that is never a silent default — a non-Development host
/// REFUSES TO START unless <c>Notifications:Email:AllowLoggingSender=true</c> explicitly acknowledges a sandbox
/// deployment that sends no email. Production fails closed.
/// <para>
/// The flag only allows the boot. It never re-enables body logging: outside Development the sender logs
/// metadata only, whatever the flag says. When a real provider lands, register it and skip this guard
/// for hosts configured onto it.
/// </para>
/// </summary>
public static class EmailSenderGuard
{
    public const string AllowLoggingSenderSetting = "Notifications:Email:AllowLoggingSender";

    public static void EnsureProductionSafe(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        if (!configuration.GetValue(AllowLoggingSenderSetting, false))
        {
            throw new InvalidOperationException(
                $"Refusing to start: the only email sender is the logging sender, which delivers nothing, and " +
                $"this host's environment is '{environment.EnvironmentName}', not Development. Configure a real " +
                $"email provider, or acknowledge a sandbox deployment that sends no email with " +
                $"{AllowLoggingSenderSetting}=true (env Notifications__Email__AllowLoggingSender=true). Outside " +
                "Development the logging sender never logs bodies or links, only metadata.");
        }
    }

    /// <summary>True when the host runs the logging sender outside Development under the sandbox acknowledgement.</summary>
    public static bool IsAcknowledgedSandbox(IConfiguration configuration, IHostEnvironment environment) =>
        !environment.IsDevelopment() && configuration.GetValue(AllowLoggingSenderSetting, false);
}
