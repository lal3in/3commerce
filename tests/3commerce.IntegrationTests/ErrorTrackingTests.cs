using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.Extensions.Hosting;
using ThreeCommerce.BuildingBlocks.Contracts.Abstractions;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.BuildingBlocks.Observability;

namespace ThreeCommerce.IntegrationTests;

/// <summary>The Sentry SDK is process-global: the tests that start it must not overlap.</summary>
[CollectionDefinition(nameof(GlobalSentrySdk), DisableParallelization = true)]
public sealed class GlobalSentrySdk;

/// <summary>
/// The GlitchTip error inbox (ADR-0067, owner decision Q5): off without a DSN; with one, an unhandled request
/// exception becomes ONE event carrying the release identity (ADR-0064) and the request's OpenTelemetry trace id —
/// the same id the 500 problem+json returns — with no request data, no user and PII scrubbed; 4xx client errors stay
/// out; the internal error probe exists outside Production only. Unit lane: a fake ingest endpoint on loopback.
/// </summary>
[Collection(nameof(GlobalSentrySdk))]
public sealed class ErrorTrackingTests
{
    private const string Sha = "c1a1c2eb61f745c551464a1fc2adf1c6c4243232";
    private const string PublicKey = "60d5721759da497aa5ca48a4c93424a1";

    private static ReleaseIdentity Release() => new("catalog", "1.0.0+c1a1c2e", Sha, "dev", "0195f0c0-0000-7000-8000-000000000001");

    [Fact]
    public void Without_a_dsn_nothing_is_registered()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ErrorTracking.DsnSetting] = "",
            [ErrorTracking.DsnEnvironmentVariable] = "",
        });

        Assert.False(builder.AddErrorTracking(Release()));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType.FullName?.StartsWith("Sentry", StringComparison.Ordinal) == true
                                                     || d.ImplementationType?.FullName?.StartsWith("Sentry", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("http://k@a:8000/1", null, "http://k@a:8000/1")]
    [InlineData(null, "http://k@b:8000/2", "http://k@b:8000/2")]
    [InlineData("  ", "http://k@b:8000/2", "http://k@b:8000/2")]
    [InlineData(null, null, null)]
    public void The_dsn_comes_from_Sentry_Dsn_then_SENTRY_DSN(string? setting, string? env, string? expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ErrorTracking.DsnSetting] = setting,
            [ErrorTracking.DsnEnvironmentVariable] = env,
        }).Build();

        Assert.Equal(expected, ErrorTracking.ResolveDsn(config));
    }

    [Fact]
    public void Options_carry_the_release_and_send_no_pii_breadcrumbs_or_propagation_headers()
    {
        var options = new SentryGenericHostOptions();
        ErrorTracking.Configure(options, $"http://{PublicKey}@localhost:8000/1", Release());

        Assert.Equal("1.0.0+c1a1c2e", options.Release);
        Assert.Equal("dev", options.Environment);
        Assert.Equal("catalog", options.ServerName);
        Assert.Equal("catalog", options.DefaultTags["service"]);
        Assert.Equal(Sha, options.DefaultTags[ReleaseIdentity.RevisionKey]);
        Assert.Equal(LogLevel.Error, options.MinimumEventLevel);
        Assert.Equal(LogLevel.None, options.MinimumBreadcrumbLevel);
        Assert.False(options.SendDefaultPii);
        Assert.False(options.IsEnvironmentUser);
        Assert.Empty(options.TracePropagationTargets);
        Assert.False(options.PropagateTraceparent);
        Assert.True(options.DisableSentryHttpMessageHandler);
        Assert.False(options.CaptureFailedRequests);
        Assert.False(options.EnableLogs);
    }

    [Fact]
    public void Prepare_stamps_the_otel_trace_and_route_and_scrubs_personal_data()
    {
        using var request = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Path = "/products/0195f0c0-0000-7000-8000-000000000001";
        http.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/products/{id}"), 0, EndpointMetadataCollection.Empty, "product"));
        using var child = new Activity("db").SetIdFormat(ActivityIdFormat.W3C).Start();

        var sentryEvent = new SentryEvent(new InvalidOperationException(
            "duplicate key (email)=(jane.doe@example.com) token=abc123 Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig"))
        {
            Message = new SentryMessage { Formatted = "Login failed for jane.doe@example.com", Message = "Login failed for {Email}" },
            User = new SentryUser { Email = "jane.doe@example.com", IpAddress = "203.0.113.9" },
        };
        sentryEvent.SentryExceptions = [new Sentry.Protocol.SentryException { Type = "InvalidOperationException", Value = "password=hunter2 for jane.doe@example.com" }];
        sentryEvent.SetTag("customer", "jane.doe@example.com");
        sentryEvent.SetExtra("detail", "reset link sent to jane.doe@example.com");

        var prepared = ErrorTracking.Prepare(sentryEvent, child, http);

        var traceId = child.TraceId.ToHexString();
        Assert.Equal(traceId, prepared.Contexts.Trace.TraceId.ToString());
        Assert.Equal(child.SpanId.ToHexString(), prepared.Contexts.Trace.SpanId.ToString());
        Assert.Equal(traceId, prepared.Tags[ErrorTracking.TraceIdTag]);
        Assert.Equal("/products/{id}", prepared.Tags["http.route"]);
        Assert.Equal("GET", prepared.Tags["http.request.method"]);

        var serialized = JsonSerializer.Serialize(new
        {
            prepared.Message?.Formatted,
            Exceptions = prepared.SentryExceptions!.Select(e => e.Value),
            prepared.Tags,
            Extra = prepared.Extra.Select(kv => kv.Value?.ToString()),
            prepared.User.Email,
            prepared.User.IpAddress,
        });
        Assert.DoesNotContain("jane.doe@example.com", serialized);
        Assert.DoesNotContain("hunter2", serialized);
        Assert.Contains(TelemetryScrubber.RedactedEmail, serialized);
        Assert.Null(prepared.User.Email);
        Assert.Null(prepared.User.IpAddress);
        Assert.Equal("password=[REDACTED] for [REDACTED_EMAIL]", prepared.SentryExceptions!.Single().Value);
    }

    [Theory]
    [InlineData("duplicate (email)=(a.b@shop.example)", "duplicate (email)=([REDACTED_EMAIL])")]
    [InlineData("GET /reset?token=Zx9_-.~ok&x=1", "GET /reset?token=[REDACTED]&x=1")]
    [InlineData("Authorization: Bearer abc.def-ghi", "Authorization: Bearer [REDACTED]")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln end", "jwt [REDACTED_TOKEN] end")]
    [InlineData("no personal data here", "no personal data here")]
    public void The_scrubber_redacts_emails_tokens_and_secrets(string input, string expected) =>
        Assert.Equal(expected, TelemetryScrubber.Scrub(input));

    [Fact]
    public void Client_errors_are_filtered_and_server_errors_are_kept()
    {
        var filter = new ClientErrorExceptionFilter();

        Assert.True(filter.Filter(new ProblemException(StatusCodes.Status409Conflict)));
        Assert.True(filter.Filter(new BadHttpRequestException("bad json", StatusCodes.Status400BadRequest)));
        Assert.False(filter.Filter(new ProblemException(StatusCodes.Status503ServiceUnavailable)));
        Assert.False(filter.Filter(new InvalidOperationException("defect")));
    }

    [Theory]
    [InlineData("Development", HttpStatusCode.InternalServerError)]
    [InlineData("Production", HttpStatusCode.NotFound)]
    public async Task The_error_probe_exists_outside_production_only(string environment, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddApiProblemDetails();
        builder.Services.AddHealthChecks();
        await using var app = builder.Build();
        app.UseApiProblemDetails();
        app.MapServiceHealth();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync(HealthEndpoints.ErrorProbePath);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task An_unhandled_request_exception_reaches_the_inbox_with_release_and_the_trace_id_of_its_500()
    {
        await using var ingest = await FakeIngest.StartAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ErrorTracking.DsnSetting] = $"http://{PublicKey}@127.0.0.1:{ingest.Port}/1",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            [ReleaseIdentity.ResourceAttributesSetting] = "deployment.environment.name=dev",
        });
        builder.AddServiceTelemetry("catalog");
        builder.Services.AddApiProblemDetails();
        builder.Services.AddHealthChecks();
        await using var app = builder.Build();
        app.UseApiProblemDetails();
        app.MapServiceHealth();
        app.MapGet("/conflict", IResult () => throw new ProblemException(StatusCodes.Status409Conflict));
        await app.StartAsync();
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/conflict")).StatusCode); // filtered: no event
        var response = await client.GetAsync(HealthEndpoints.ErrorProbePath);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        // The problem+json traceId is the W3C id of the request activity (00-<trace id>-<span id>-<flags>).
        var problemTraceId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("traceId").GetString()!;
        var traceId = problemTraceId.Split('-')[1];
        await SentrySdk.FlushAsync(TimeSpan.FromSeconds(10));

        var probeEvents = await ingest.WaitForEventsAsync(e => e.Contains(nameof(ErrorProbeException), StringComparison.Ordinal));
        var json = Assert.Single(probeEvents);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(app.Services.GetRequiredService<ReleaseIdentity>().ServiceVersion, root.GetProperty("release").GetString());
        Assert.Equal("dev", root.GetProperty("environment").GetString());
        Assert.Equal("catalog", root.GetProperty("server_name").GetString());
        Assert.Equal(traceId, root.GetProperty("contexts").GetProperty("trace").GetProperty("trace_id").GetString());
        Assert.Equal(traceId, root.GetProperty("tags").GetProperty(ErrorTracking.TraceIdTag).GetString());
        Assert.Equal(HealthEndpoints.ErrorProbePath, root.GetProperty("tags").GetProperty("http.route").GetString());
        // No request data at all (URL, query, headers, cookies, body, client IP): at most an empty object.
        Assert.True(!root.TryGetProperty("request", out var request) || !request.EnumerateObject().Any(), request.ToString());
        // Log scopes are not pushed into Sentry at all (ScopelessLogger): no RequestPath, no RequestId.
        Assert.False(root.GetProperty("tags").TryGetProperty("RequestPath", out _));
        Assert.False(root.GetProperty("tags").TryGetProperty("RequestId", out _));
        Assert.False(root.TryGetProperty("breadcrumbs", out var crumbs) && crumbs.ValueKind == JsonValueKind.Array && crumbs.GetArrayLength() > 0);
        Assert.DoesNotContain(probeEvents.Concat(ingest.Events), e => e.Contains("ProblemException", StringComparison.Ordinal));

        await app.StopAsync();
    }

    private sealed class ProblemException(int status) : Exception("domain failure"), IProblemException
    {
        public int StatusCode => status;
        public string ErrorCode => "test.problem";
    }

    /// <summary>A loopback stand-in for GlitchTip's envelope endpoint: records every event item it receives.</summary>
    private sealed class FakeIngest : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<string> _events = new();

        private FakeIngest(WebApplication app) => _app = app;

        public int Port { get; private set; }
        public IReadOnlyCollection<string> Events => _events.ToArray();

        public static async Task<FakeIngest> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var ingest = new FakeIngest(app);
            app.MapPost("/api/{project}/envelope/", async (HttpRequest request) =>
            {
                Stream body = request.Body;
                if (request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
                {
                    body = new GZipStream(body, CompressionMode.Decompress);
                }

                using var reader = new StreamReader(body, Encoding.UTF8);
                var lines = (await reader.ReadToEndAsync()).Split('\n');
                // Envelope: header line, then (item header, item payload) pairs.
                for (var i = 1; i + 1 < lines.Length; i += 2)
                {
                    if (lines[i].Contains("\"type\":\"event\"", StringComparison.Ordinal))
                    {
                        ingest._events.Enqueue(lines[i + 1]);
                    }
                }

                return Results.Ok(new { id = Guid.NewGuid().ToString("N") });
            });
            await app.StartAsync();
            ingest.Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
            return ingest;
        }

        public async Task<List<string>> WaitForEventsAsync(Func<string, bool> match)
        {
            for (var i = 0; i < 100; i++)
            {
                var found = _events.Where(match).ToList();
                if (found.Count > 0)
                {
                    await Task.Delay(300); // a duplicate would arrive in the same flush
                    return _events.Where(match).ToList();
                }

                await Task.Delay(100);
            }

            return [];
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
