using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// The Notifications logging sender must not leak bearer secrets (observability audit P0-2): only a
/// Development host logs the full email (links included); every other environment logs metadata only —
/// no body, link, token or address. A non-Development host refuses to boot onto the logging sender unless
/// <c>Notifications:Email:AllowLoggingSender=true</c> acknowledges a sandbox. Unit lane: no Docker.
/// </summary>
public sealed class NotificationEmailSafetyTests
{
    private const string Address = "Jane.Doe@example.com";
    private const string ResetToken = "q9Zr-5xK_TsECRET-reset-token";
    private const string VerifyToken = "vT0k3n_verify-SECRET";

    private static readonly EmailTemplates Templates = new("https://shop.example");

    [Fact]
    public async Task Development_logs_the_full_email_including_the_link()
    {
        var logger = new CapturingLogger<LoggingEmailSender>();
        var sender = new LoggingEmailSender(logger, Env(Environments.Development));

        await sender.SendAsync(Templates.PasswordReset(Address, ResetToken), CancellationToken.None);

        var line = Assert.Single(logger.Lines);
        Assert.Contains(Address, line);
        Assert.Contains("reset-password?token=" + ResetToken, line); // e2e-verify --live greps exactly this
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Container")]
    public async Task Outside_development_no_link_token_or_address_is_logged(string environment)
    {
        var logger = new CapturingLogger<LoggingEmailSender>();
        var sender = new LoggingEmailSender(logger, Env(environment));

        await sender.SendAsync(Templates.PasswordReset(Address, ResetToken), CancellationToken.None);
        await sender.SendAsync(Templates.Verification(Address, VerifyToken), CancellationToken.None);
        var orderId = Guid.CreateVersion7();
        await sender.SendAsync(Templates.OrderConfirmed(Address, orderId, 1999, "AUD"), CancellationToken.None);

        Assert.Equal(3, logger.Lines.Count);
        foreach (var record in logger.Records)
        {
            var everything = record.Message + " " + string.Join(" ", record.State.Select(kv => $"{kv.Key}={kv.Value}"));
            Assert.DoesNotContain(ResetToken, everything);
            Assert.DoesNotContain(VerifyToken, everything);
            Assert.DoesNotContain("token=", everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://shop.example", everything);
            Assert.DoesNotContain("@", everything);
            Assert.DoesNotContain("jane.doe", everything, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(LoggingEmailSender.RecipientHash(Address), record.Message);
        }

        // Non-secret metadata stays: subject and the delivery reference.
        Assert.Contains("Reset your password", logger.Lines[0]);
        Assert.Contains(NotificationReferences.OrderConfirmed(orderId), logger.Lines[2]);
    }

    [Fact]
    public void Recipient_hash_is_stable_short_and_case_insensitive()
    {
        var hash = LoggingEmailSender.RecipientHash(Address);

        Assert.Equal(hash, LoggingEmailSender.RecipientHash("  jane.doe@EXAMPLE.com "));
        Assert.NotEqual(hash, LoggingEmailSender.RecipientHash("john@example.com"));
        Assert.Matches("^rcpt-[0-9a-f]{12}$", hash);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Container")]
    public void Guard_refuses_a_non_development_host_without_the_sandbox_acknowledgement(string environment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EmailSenderGuard.EnsureProductionSafe(Config(), Env(environment)));
        Assert.Contains(EmailSenderGuard.AllowLoggingSenderSetting, ex.Message);
    }

    [Fact]
    public void Guard_refuses_production_when_the_acknowledgement_is_explicitly_false()
    {
        Assert.Throws<InvalidOperationException>(() => EmailSenderGuard.EnsureProductionSafe(
            Config((EmailSenderGuard.AllowLoggingSenderSetting, "false")), Env(Environments.Production)));
    }

    [Fact]
    public void Guard_allows_development_without_the_flag()
    {
        EmailSenderGuard.EnsureProductionSafe(Config(), Env(Environments.Development));
        Assert.False(EmailSenderGuard.IsAcknowledgedSandbox(Config(), Env(Environments.Development)));
    }

    [Fact]
    public void Guard_allows_an_acknowledged_sandbox_outside_development()
    {
        var config = Config((EmailSenderGuard.AllowLoggingSenderSetting, "true"));

        EmailSenderGuard.EnsureProductionSafe(config, Env(Environments.Staging));
        Assert.True(EmailSenderGuard.IsAcknowledgedSandbox(config, Env(Environments.Staging)));
    }

    [Fact]
    public void Production_notifications_host_without_the_acknowledgement_refuses_to_boot()
    {
        // The real Program: proves the guard runs at boot, before the DB/bus are touched (no Docker needed).
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var factory = new WebApplicationFactory<LoggingEmailSender>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Production);
            b.UseSetting("InternalAuth:PublicKey", ecdsa.ExportSubjectPublicKeyInfoPem());
            // Unreachable on purpose: should the guard ever regress, this test fails without touching a real DB/broker.
            b.UseSetting("ConnectionStrings:Database", "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2");
            b.UseSetting("ConnectionStrings:RabbitMq", "amqp://guest:guest@127.0.0.1:1");
        });

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains(EmailSenderGuard.AllowLoggingSenderSetting, Flatten(ex));
    }

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static IHostEnvironment Env(string name) => new FakeHostEnvironment { EnvironmentName = name };

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "notifications-tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Records { get; } = [];

        public List<string> Lines => Records.Select(r => r.Message).ToList();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Records.Add((formatter(state, exception), values));
        }
    }
}
