using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Observability;

/// <summary>
/// Which release is running, and where (ADR-0064). Every OTel resource carries it, so each span, metric and
/// log can be tied to the commit that produced it — ADR-0058 promotions fast-forward one tested commit, so the
/// commit SHA IS the release.
/// <list type="bullet">
/// <item><c>service.version</c> / <c>vcs.ref.head.revision</c> come from the assembly's
/// <see cref="AssemblyInformationalVersionAttribute"/> (<c>&lt;Version&gt;+&lt;SourceRevisionId&gt;</c>): the SDK
/// stamps the git SHA on a local build, Dockerfiles pass <c>-p:SourceRevisionId=$GIT_SHA</c>, and bare-run adds
/// <c>-dirty</c> for an uncommitted tree.</item>
/// <item><c>deployment.environment.name</c> maps the host environment (Development → dev, Staging/Test → test,
/// Production → prod).</item>
/// <item>An explicit <c>OTEL_RESOURCE_ATTRIBUTES</c> entry for any of these keys always wins (Helm sets the
/// environment per release stage; CI sets <c>ci</c>).</item>
/// </list>
/// </summary>
public sealed partial record ReleaseIdentity(
    string ServiceName,
    string ServiceVersion,
    string? Revision,
    string Environment,
    string InstanceId)
{
    public const string ServiceVersionKey = "service.version";
    public const string RevisionKey = "vcs.ref.head.revision";
    public const string EnvironmentKey = "deployment.environment.name";
    public const string InstanceIdKey = "service.instance.id";
    public const string ResourceAttributesSetting = "OTEL_RESOURCE_ATTRIBUTES";

    /// <summary>Used when an assembly carries no informational version at all.</summary>
    public const string UnknownVersion = "unknown";

    private const string DirtySuffix = "-dirty";

    /// <summary>
    /// Resolves the identity for <paramref name="serviceName"/>: build metadata from
    /// <paramref name="informationalVersion"/>, the environment from <paramref name="hostEnvironmentName"/>, then
    /// any explicit <c>OTEL_RESOURCE_ATTRIBUTES</c> entry in <paramref name="configuration"/> on top.
    /// </summary>
    public static ReleaseIdentity Resolve(
        string serviceName,
        IConfiguration configuration,
        string? hostEnvironmentName,
        string? informationalVersion)
    {
        var (version, revision) = ParseInformationalVersion(informationalVersion);
        var explicitAttributes = ParseResourceAttributes(configuration[ResourceAttributesSetting]);

        return new ReleaseIdentity(
            serviceName,
            explicitAttributes.GetValueOrDefault(ServiceVersionKey) ?? version,
            explicitAttributes.GetValueOrDefault(RevisionKey) ?? revision,
            explicitAttributes.GetValueOrDefault(EnvironmentKey) ?? MapEnvironment(hostEnvironmentName),
            explicitAttributes.GetValueOrDefault(InstanceIdKey) ?? Guid.CreateVersion7().ToString());
    }

    /// <summary>
    /// Splits <c>&lt;version&gt;+&lt;metadata&gt;</c>. A metadata part that is a commit SHA (7–40 hex, optional
    /// <c>-dirty</c>) yields the full SHA as the revision and <c>&lt;version&gt;+&lt;sha7&gt;[-dirty]</c> as the
    /// service version; anything else is kept verbatim as the version with no revision. Never throws.
    /// </summary>
    public static (string Version, string? Revision) ParseInformationalVersion(string? informationalVersion)
    {
        var value = informationalVersion?.Trim();
        if (string.IsNullOrEmpty(value))
            return (UnknownVersion, null);

        var plus = value.IndexOf('+');
        if (plus < 0)
            return (value, null);

        var version = plus == 0 ? UnknownVersion : value[..plus];
        var metadata = value[(plus + 1)..];
        var dirty = metadata.EndsWith(DirtySuffix, StringComparison.OrdinalIgnoreCase);
        var sha = dirty ? metadata[..^DirtySuffix.Length] : metadata;
        if (!CommitSha().IsMatch(sha))
            return (metadata.Length == 0 ? version : value, null);

        sha = sha.ToLowerInvariant();
        return ($"{version}+{sha[..7]}{(dirty ? DirtySuffix : "")}", sha);
    }

    /// <summary>Host environment name → <c>deployment.environment.name</c> (dev / test / prod / ci, else lower-cased).</summary>
    public static string MapEnvironment(string? hostEnvironmentName) =>
        hostEnvironmentName?.Trim().ToLowerInvariant() switch
        {
            null or "" => "unknown",
            "development" or "dev" => "dev",
            "staging" or "test" => "test",
            "production" or "prod" => "prod",
            "ci" => "ci",
            var other => other,
        };

    /// <summary>
    /// Parses <c>OTEL_RESOURCE_ATTRIBUTES</c> (<c>key=value</c> pairs, comma-separated, values percent-encoded per
    /// the OTel spec). Malformed pairs are skipped; the last duplicate wins.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseResourceAttributes(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = pair[..eq].Trim();
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Trim());
            if (key.Length > 0 && value.Length > 0)
                result[key] = value;
        }

        return result;
    }

    /// <summary>The release attributes for an OTel resource (service.name/version/instance + revision + environment).</summary>
    public ResourceBuilder Configure(ResourceBuilder resource)
    {
        resource.AddService(ServiceName, serviceVersion: ServiceVersion, autoGenerateServiceInstanceId: false, serviceInstanceId: InstanceId);
        var attributes = new List<KeyValuePair<string, object>> { new(EnvironmentKey, Environment) };
        if (Revision is not null)
            attributes.Add(new(RevisionKey, Revision));
        return resource.AddAttributes(attributes);
    }

    /// <summary>The informational version of the running app (the host's ApplicationName assembly, else the entry assembly).</summary>
    public static string? InformationalVersionOf(IHostEnvironment environment)
    {
        Assembly? assembly = null;
        try
        {
            if (!string.IsNullOrEmpty(environment.ApplicationName))
                assembly = Assembly.Load(new AssemblyName(environment.ApplicationName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException or ArgumentException)
        {
            // Fall through to the entry assembly — a missing SHA must never stop a service from starting.
        }

        assembly ??= Assembly.GetEntryAssembly() ?? typeof(ReleaseIdentity).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    }

    [GeneratedRegex("^[0-9a-fA-F]{7,40}$")]
    private static partial Regex CommitSha();
}

public static class ReleaseIdentityExtensions
{
    /// <summary>
    /// Resolves this host's <see cref="ReleaseIdentity"/>, registers it as a singleton and logs it once at
    /// startup ("Release identity: …" — what <c>scripts/e2e-verify.sh</c> L2b reads). Idempotent per host.
    /// </summary>
    public static ReleaseIdentity AddReleaseIdentity<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        var existing = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(ReleaseIdentity))?.ImplementationInstance;
        if (existing is ReleaseIdentity already)
            return already;

        var identity = ReleaseIdentity.Resolve(
            serviceName,
            builder.Configuration,
            builder.Environment.EnvironmentName,
            ReleaseIdentity.InformationalVersionOf(builder.Environment));
        builder.Services.AddSingleton(identity);
        builder.Services.AddHostedService<ReleaseIdentityStartupLog>();
        return identity;
    }
}

internal sealed partial class ReleaseIdentityStartupLog(ReleaseIdentity identity, ILogger<ReleaseIdentityStartupLog> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogIdentity(logger, identity.ServiceName, identity.ServiceVersion, identity.Revision ?? "unknown",
            identity.Environment, identity.InstanceId);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Release identity: service.name={ServiceName} service.version={ServiceVersion} vcs.ref.head.revision={Revision} deployment.environment.name={Environment} service.instance.id={InstanceId}")]
    private static partial void LogIdentity(ILogger logger, string serviceName, string serviceVersion, string revision,
        string environment, string instanceId);
}
