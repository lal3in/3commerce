using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.Extensions.Hosting;
using Sentry.Protocol;

namespace ThreeCommerce.BuildingBlocks.Observability;

/// <summary>
/// The error inbox (ADR-0067, owner decision Q5): every <c>Error</c>/<c>Critical</c> log record that a .NET host
/// writes — unhandled request exceptions (logged by the exception handler), message-consumer faults, scheduled-job
/// failures, Blazor circuit errors — becomes an event in GlitchTip, the self-hosted Sentry-API-compatible inbox
/// that groups them into issues (first seen, regressions, assign/resolve).
/// <list type="bullet">
/// <item><b>Off unless configured:</b> only with a DSN (<c>Sentry:Dsn</c>, or the conventional <c>SENTRY_DSN</c>
/// environment variable). Unset = nothing registered, nothing sent.</item>
/// <item><b>Release identity (ADR-0064):</b> <c>release</c> = <c>service.version</c>, <c>environment</c> =
/// <c>deployment.environment.name</c>, tags <c>service</c> / <c>service.instance.id</c> / <c>vcs.ref.head.revision</c>.</item>
/// <item><b>Trace id:</b> the current OpenTelemetry trace + span ids go into the event's trace context and a
/// searchable <c>trace_id</c> tag, so an issue leads to its trace in Tempo and its logs in Loki.</item>
/// <item><b>Privacy:</b> the ILogger integration only — no <c>Sentry.AspNetCore</c>, so no request URL, headers,
/// cookies, body or client IP; no user; no breadcrumbs (without a per-request scope they would mix requests); no
/// Sentry tracing, no <c>sentry-trace</c>/<c>baggage</c> headers on outgoing calls (OpenTelemetry owns
/// propagation), no HTTP-client capture. Messages, exception values and tags are scrubbed by
/// <see cref="TelemetryScrubber"/> before sending — the same patterns as the collector's <c>transform/redact</c>.</item>
/// </list>
/// </summary>
public static class ErrorTracking
{
    /// <summary>The configuration key of the DSN (<c>Sentry__Dsn</c> as an environment variable).</summary>
    public const string DsnSetting = "Sentry:Dsn";

    /// <summary>The Sentry SDKs' conventional environment variable, accepted as well.</summary>
    public const string DsnEnvironmentVariable = "SENTRY_DSN";

    /// <summary>The tag every event carries with the 32-hex OpenTelemetry trace id (searchable in GlitchTip).</summary>
    public const string TraceIdTag = "trace_id";

    /// <summary>The configured DSN, or null when error tracking is off.</summary>
    public static string? ResolveDsn(IConfiguration configuration)
    {
        foreach (var key in new[] { DsnSetting, DsnEnvironmentVariable })
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Registers the Sentry logger provider when a DSN is configured. Returns whether it did.
    /// <paramref name="configure"/> lets a host add its own filters (e.g. client errors that are not defects).
    /// </summary>
    public static bool AddErrorTracking(
        this IHostApplicationBuilder builder,
        ReleaseIdentity release,
        Action<SentryGenericHostOptions>? configure = null)
    {
        var dsn = ResolveDsn(builder.Configuration);
        if (dsn is null)
        {
            return false;
        }

        builder.Services.AddHttpContextAccessor();
        var before = builder.Services.Count;
        builder.UseSentry(options =>
        {
            Configure(options, dsn, release);
            configure?.Invoke(options);
        });
        WithoutLogScopes(builder.Services, before);
        return true;
    }

    /// <summary>
    /// Sentry's logger turns EVERY ILogger scope into a pushed (cloned) SDK scope — one per request, per consumed
    /// message, per EF/MassTransit scope — only to copy the scope state into tags. Our events take their context from
    /// the trace id and the route instead, the scope state is mostly the raw RequestPath we drop anyway, and the copy
    /// costs on hot paths (a cold catalog import logs ~650k records). So the Sentry logger providers that
    /// <c>UseSentry</c> just registered are wrapped in a decorator whose loggers forward Log/IsEnabled and make
    /// BeginScope a no-op.
    /// </summary>
    private static void WithoutLogScopes(IServiceCollection services, int firstNew)
    {
        for (var i = firstNew; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(ILoggerProvider) || descriptor.ImplementationFactory is not { } factory)
            {
                continue;
            }

            services[i] = ServiceDescriptor.Singleton<ILoggerProvider>(sp => new ScopelessLoggerProvider((ILoggerProvider)factory(sp)));
        }
    }

    private sealed class ScopelessLoggerProvider(ILoggerProvider inner) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ScopelessLogger(inner.CreateLogger(categoryName));

        public void Dispose() => inner.Dispose();
    }

    private sealed class ScopelessLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    /// <summary>The option set <see cref="AddErrorTracking"/> applies (public for the unit tests).</summary>
    public static void Configure(SentryGenericHostOptions options, string dsn, ReleaseIdentity release)
    {
        options.Dsn = dsn;
        options.Release = release.ServiceVersion;
        options.Environment = release.Environment;
        options.ServerName = release.ServiceName;
        options.DefaultTags["service"] = release.ServiceName;
        options.DefaultTags[ReleaseIdentity.InstanceIdKey] = release.InstanceId;
        if (!string.IsNullOrEmpty(release.Revision))
        {
            options.DefaultTags[ReleaseIdentity.RevisionKey] = release.Revision;
        }

        // Errors only; no breadcrumbs (see the type summary).
        options.MinimumEventLevel = LogLevel.Error;
        options.MinimumBreadcrumbLevel = LogLevel.None;
        options.MaxBreadcrumbs = 0;

        // No PII, no Sentry tracing, no outgoing-request instrumentation: OpenTelemetry owns traces and propagation.
        options.SendDefaultPii = false;
        options.IsEnvironmentUser = false;
        options.AutoSessionTracking = false;
        options.TracesSampleRate = 0;
        options.TracePropagationTargets = [];
        options.PropagateTraceparent = false;
        options.DisableSentryHttpMessageHandler = true;
        options.CaptureFailedRequests = false;
        options.DisableDiagnosticSourceIntegration();
        options.AttachStacktrace = true;

        options.EnableLogs = false;
        // HttpContextAccessor reads the ambient request (the host sets it once an accessor is registered).
        var http = new HttpContextAccessor();
        options.SetBeforeSend((sentryEvent, _) => Prepare(sentryEvent, Activity.Current, http.HttpContext));
    }

    /// <summary>Stamps the OpenTelemetry trace context and the request's route and scrubs the event (public for the unit tests).</summary>
    public static SentryEvent Prepare(SentryEvent sentryEvent, Activity? activity, HttpContext? httpContext = null)
    {
        if (activity is not null && activity.TraceId != default)
        {
            var traceId = activity.TraceId.ToHexString();
            sentryEvent.Contexts.Trace.TraceId = SentryId.Parse(traceId);
            sentryEvent.Contexts.Trace.SpanId = SpanId.Parse(activity.SpanId.ToHexString());
            sentryEvent.SetTag(TraceIdTag, traceId);
        }

        // The route TEMPLATE of the failing request (never the raw path or query: they can hold ids and tokens).
        // The exception handler clears the endpoint before it logs, but keeps the original in IExceptionHandlerFeature.
        var endpoint = httpContext?.GetEndpoint() ?? httpContext?.Features.Get<IExceptionHandlerFeature>()?.Endpoint;
        if (endpoint is RouteEndpoint { RoutePattern.RawText: { Length: > 0 } route })
        {
            sentryEvent.SetTag("http.route", route);
            sentryEvent.SetTag("http.request.method", httpContext!.Request.Method);
        }

        return TelemetryScrubber.Scrub(sentryEvent);
    }
}

/// <summary>
/// Redacts personal data and secrets from an error event before it leaves the process — the same patterns as the
/// collector's <c>transform/redact</c> (emails, <c>token=</c>, <c>password=</c>, bearer tokens) plus JWTs.
/// Exception messages are the risky part: a Postgres unique violation, for one, can quote the email it rejected.
/// </summary>
public static partial class TelemetryScrubber
{
    public const string RedactedEmail = "[REDACTED_EMAIL]";

    /// <summary>Tags never sent: raw request paths (ASP.NET Core's hosting log scope adds <c>RequestPath</c>).</summary>
    private static readonly HashSet<string> DroppedTags = new(StringComparer.OrdinalIgnoreCase) { "RequestPath", "Path", "url.path", "url.full" };

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)\bbearer [A-Za-z0-9._~+/=-]+")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)\b(token|password|passwd|secret|api_?key)=[^&\s""',;]+")]
    private static partial Regex SecretPair();

    /// <summary>Scrubs one string (null stays null).</summary>
    public static string? Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var result = Jwt().Replace(value, "[REDACTED_TOKEN]");
        result = Bearer().Replace(result, "Bearer [REDACTED]");
        result = SecretPair().Replace(result, m => $"{m.Groups[1].Value}=[REDACTED]");
        return Email().Replace(result, RedactedEmail);
    }

    /// <summary>Scrubs an event in place and returns it.</summary>
    public static SentryEvent Scrub(SentryEvent sentryEvent)
    {
        if (sentryEvent.Message is { } message)
        {
            message.Message = Scrub(message.Message);
            message.Formatted = Scrub(message.Formatted);
            message.Params = null;
        }

        foreach (var exception in sentryEvent.SentryExceptions ?? [])
        {
            exception.Value = Scrub(exception.Value);
            if (exception.Mechanism is { } mechanism)
            {
                foreach (var key in mechanism.Data.Keys.ToList())
                {
                    if (mechanism.Data[key] is string text)
                    {
                        mechanism.Data[key] = Scrub(text)!;
                    }
                }
            }
        }

        foreach (var (key, value) in sentryEvent.Tags.ToList())
        {
            // Logging scopes become tags; the hosting scope's raw request path can carry ids or a token (the route
            // template is tagged instead, ErrorTracking.Prepare).
            if (DroppedTags.Contains(key))
            {
                sentryEvent.UnsetTag(key);
            }
            else if (key != ErrorTracking.TraceIdTag)
            {
                sentryEvent.SetTag(key, Scrub(value) ?? string.Empty);
            }
        }

        foreach (var (key, value) in sentryEvent.Extra.ToList())
        {
            if (value is string text)
            {
                sentryEvent.SetExtra(key, Scrub(text));
            }
        }

        sentryEvent.User = new SentryUser();
        sentryEvent.Request = new SentryRequest();
        return sentryEvent;
    }
}
