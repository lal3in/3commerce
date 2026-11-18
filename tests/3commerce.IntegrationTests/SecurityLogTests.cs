using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Authz;
using ThreeCommerce.Gateway.Auth;
using ThreeCommerce.Identity.Infrastructure;
using ActionDecision = ThreeCommerce.BuildingBlocks.Infrastructure.Authz.ActionDecision;
using PolicyDecisionResponse = ThreeCommerce.BuildingBlocks.Infrastructure.Authz.PolicyDecisionResponse;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane (ADR-0065 Q2, the 12-month security-log stream): failed logins, lockouts, MFA failures, permission
/// denials and rejected gateway sessions are logged under logger categories the collector's
/// <c>transform/security_category</c> labels <c>log_category=security</c> — the regex is read from the REAL collector
/// config, so renaming a class or the regex fails here — with opaque ids only (never an email, password or token).
/// </summary>
public sealed class SecurityLogTests
{
    private static readonly Guid User = Guid.Parse("01930000-0000-7000-8000-0000000000c1");
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Theory]
    [InlineData(typeof(AuthService))]
    [InlineData(typeof(PermissionGate))]
    [InlineData(typeof(SessionAuthMiddleware))]
    public void The_security_loggers_categories_are_labelled_security_by_the_collector(Type loggerCategory) =>
        Assert.Matches(SecurityCategory(), loggerCategory.FullName!);

    [Theory]
    [InlineData("ThreeCommerce.Catalog.Api.Endpoints.ProductEndpoints")]
    [InlineData("ThreeCommerce.Identity.Infrastructure.IdentityDbContext")]
    public void Ordinary_categories_are_not_security(string category) =>
        Assert.DoesNotMatch(SecurityCategory(), category);

    [Fact]
    public void Auth_security_lines_carry_ids_and_counts_never_the_email_or_secret()
    {
        var logger = new CapturingLogger<AuthService>();
        var until = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

        logger.LoginUnknownAccount(Tenant);
        logger.LoginWrongPassword(User, Tenant, 3);
        logger.AccountLockedOut(User, Tenant, 5, until, "password");
        logger.LoginRefusedLockedOut(User, Tenant, until);
        logger.MfaChallengeFailed(User, Tenant, 2);
        logger.MfaChallengeRefusedLockedOut(User, Tenant, until);
        logger.MfaStepUpFailed(User, Tenant);
        logger.MfaEnrollmentConfirmFailed(User, Tenant);
        logger.LoginSucceeded(User, Tenant, mfaPending: true);

        Assert.Equal(9, logger.Entries.Count);
        Assert.All(logger.Entries, e =>
        {
            Assert.Equal("ThreeCommerce.Identity.Infrastructure.AuthService", e.Category);
            Assert.StartsWith("Security: ", e.Message, StringComparison.Ordinal);
            Assert.Contains(Tenant.ToString(), e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("@", e.Message, StringComparison.Ordinal);
        });
        Assert.Equal(8, logger.Entries.Count(e => e.Level == LogLevel.Warning));
        Assert.Contains(logger.Entries, e => e.Message.Contains("locked out after 5 failed attempts", StringComparison.Ordinal)
                                             && e.State["UserId"] is Guid id && id == User);
    }

    [Fact]
    public async Task A_permission_denial_is_security_logged_with_the_permission_principal_and_tenant()
    {
        var logger = new CapturingLogger<PermissionGate>();
        var gate = Gate(logger, _ => JsonResponse(new PolicyDecisionResponse(Guid.CreateVersion7(), false,
            [new ActionDecision("payment.refund", false, default, false, false)], [])));

        var verdict = await gate.CheckAsync(Caller(), "payment.refund", CancellationToken.None);

        Assert.Equal(PermissionVerdict.Denied, verdict);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("payment.refund", entry.State["Permission"]);
        Assert.Equal(User, entry.State["UserId"]);
        Assert.Equal(Tenant.ToString(), entry.State["TenantId"]);
        Assert.Contains("not granted", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("@", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_with_no_principal_in_the_tenant_and_one_without_a_subject_are_both_logged_denials()
    {
        var logger = new CapturingLogger<PermissionGate>();
        var gate = Gate(logger, _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Equal(PermissionVerdict.Denied, await gate.CheckAsync(Caller(), "payment.refund", CancellationToken.None));
        Assert.Equal(PermissionVerdict.Denied, await gate.CheckAsync(new ClaimsPrincipal(new ClaimsIdentity()), "payment.refund", CancellationToken.None));

        Assert.Equal(2, logger.Entries.Count);
        Assert.Contains("no active principal in tenant", logger.Entries[0].Message, StringComparison.Ordinal);
        Assert.Contains("without a principal id", logger.Entries[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_allowed_action_logs_nothing()
    {
        var logger = new CapturingLogger<PermissionGate>();
        var gate = Gate(logger, _ => JsonResponse(new PolicyDecisionResponse(Guid.CreateVersion7(), false,
            [new ActionDecision("payment.refund", true, default, false, false)], [])));

        Assert.Equal(PermissionVerdict.Allowed, await gate.CheckAsync(Caller(), "payment.refund", CancellationToken.None));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task The_gateway_security_logs_a_rejected_session_once_per_token_per_minute_without_the_token()
    {
        var logger = new CapturingLogger<SessionAuthMiddleware>();
        var middleware = new SessionAuthMiddleware(
            _ => Task.CompletedTask,
            new MemoryCache(new MemoryCacheOptions()),
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)),
            minter: null!, // never reached: the session is rejected
            logger);
        const string token = "stale-session-token-value";

        await middleware.InvokeAsync(Request(token, "/api/ordering/orders"));
        await middleware.InvokeAsync(Request(token, "/api/catalog/products"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("ThreeCommerce.Gateway.Auth.SessionAuthMiddleware", entry.Category);
        Assert.Contains("introspection 401", entry.Message, StringComparison.Ordinal);
        Assert.Contains("/api/ordering/orders", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, entry.Message, StringComparison.Ordinal);
        Assert.Equal(12, ((string)entry.State["SessionRef"]!).Length);
    }

    private static Regex SecurityCategory()
    {
        var config = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "observability", "otel-collector-config.yaml"));
        var match = Regex.Match(config, "IsMatch\\(instrumentation_scope\\.name, \"(?<rx>[^\"]+)\"\\)'\\s*\\n\\s*groupbyattrs");
        Assert.True(match.Success, "transform/security_category's IsMatch regex not found in the collector config");
        return new Regex(match.Groups["rx"].Value);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "3commerce.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static PermissionGate Gate(ILogger<PermissionGate> logger, Func<HttpRequestMessage, HttpResponseMessage> pdp) =>
        new(new PolicyDecisionClient(new HttpClient(new StubHandler(pdp)) { BaseAddress = new Uri("http://identity.test") }),
            audit: null!, config: null!, logger);

    private static ClaimsPrincipal Caller() => new(new ClaimsIdentity(
        [new Claim("sub", User.ToString()), new Claim("tenant", Tenant.ToString()), new Claim("email", "ops@example.com")], "internal"));

    private static HttpResponseMessage JsonResponse<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private static DefaultHttpContext Request(string token, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers.Cookie = $"{SessionAuthMiddleware.CookieName}={token}";
        return context;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(respond)) { BaseAddress = new Uri("http://identity.test") };
    }
}

/// <summary>An ILogger&lt;T&gt; that records category, level, formatted message and structured state.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State);

    public List<Entry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
        Entries.Add(new Entry(typeof(T).FullName!, logLevel, formatter(state, exception), values.ToDictionary(kv => kv.Key, kv => kv.Value)));
    }
}
