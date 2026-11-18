using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0065 Q2 through the REAL Identity API on Postgres: wrong passwords, the lockout they trigger and a login
/// refused while locked out are security-logged under the AuthService category (labelled log_category=security by
/// the collector, SecurityLogTests) with the user id — and the email never appears in any of those lines.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class SecurityLogIntegrationTests(Phase2Fixture fixture)
{
    [Fact]
    public async Task Failed_logins_and_the_lockout_are_security_logged_by_id_never_by_email()
    {
        var sink = new SinkProvider();
        using var identity = fixture.CreateIdentityFactory();
        using var client = identity.CreateClient(new() { HandleCookies = false });
        // Added to the running host (no WithWebHostBuilder: a derived factory escapes the fixture's host tracking);
        // LoggerFactory.AddProvider attaches to loggers created before it too.
        identity.Services.GetRequiredService<ILoggerFactory>().AddProvider(sink);
        var email = $"seclog-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/register", new { email, password = "a-strong-password" });

        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/login", new { email, password = "wrong-password-xx" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/login", new { email, password = "a-strong-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/login", new { email = $"nobody-{Guid.NewGuid():N}@example.com", password = "x-strong-password" })).StatusCode);

        var security = sink.Lines
            .Where(l => l.Category == "ThreeCommerce.Identity.Infrastructure.AuthService" && l.Message.StartsWith("Security: ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(4, security.Count(l => l.Message.Contains("wrong password", StringComparison.Ordinal)));
        var lockout = Assert.Single(security, l => l.Message.Contains("locked out after 5 failed attempts", StringComparison.Ordinal));
        Assert.Contains(security, l => l.Message.Contains("login refused — account locked out", StringComparison.Ordinal));
        Assert.Contains(security, l => l.Message.Contains("no such account", StringComparison.Ordinal));
        Assert.Matches("user [0-9a-f-]{36}", lockout.Message);
        Assert.All(security, l => Assert.DoesNotContain(email, l.Message, StringComparison.OrdinalIgnoreCase));
        Assert.All(security, l => Assert.DoesNotContain("@", l.Message, StringComparison.Ordinal));
    }

    private sealed record Line(string Category, LogLevel Level, string Message);

    private sealed class SinkProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<Line> lines = new();

        public IReadOnlyList<Line> Lines => [.. lines];

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, lines);

        public void Dispose()
        {
        }

        private sealed class Sink(string category, ConcurrentQueue<Line> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(new Line(category, logLevel, formatter(state, exception)));
        }
    }
}
