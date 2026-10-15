using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Release stamping (ADR-0064, observability audit P0-1): every OTel resource carries service.version,
/// vcs.ref.head.revision and deployment.environment.name from build metadata + host environment, an explicit
/// OTEL_RESOURCE_ATTRIBUTES entry wins, and a build without a SHA still starts. Unit lane — no Docker.
/// </summary>
public sealed class ReleaseIdentityTests
{
    private const string Sha = "c1a1c2eb61f745c551464a1fc2adf1c6c4243232";

    private static IConfiguration Config(string? resourceAttributes = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Always present (empty = "no explicit attributes"), so a value in the runner's environment
                // (CI sets deployment.environment.name=ci) can never leak into these assertions.
                [ReleaseIdentity.ResourceAttributesSetting] = resourceAttributes ?? "",
            })
            .Build();

    private static Dictionary<string, object> Attributes(ReleaseIdentity identity) =>
        identity.Configure(ResourceBuilder.CreateEmpty()).Build().Attributes.ToDictionary(a => a.Key, a => a.Value);

    [Fact]
    public void A_stamped_build_yields_version_revision_and_environment_on_the_resource()
    {
        var identity = ReleaseIdentity.Resolve("catalog", Config(), "Development", $"1.0.0+{Sha}");

        var attributes = Attributes(identity);
        Assert.Equal("catalog", attributes["service.name"]);
        Assert.Equal("1.0.0+c1a1c2e", attributes[ReleaseIdentity.ServiceVersionKey]);
        Assert.Equal(Sha, attributes[ReleaseIdentity.RevisionKey]);
        Assert.Equal("dev", attributes[ReleaseIdentity.EnvironmentKey]);
        Assert.Equal(identity.InstanceId, attributes[ReleaseIdentity.InstanceIdKey]);
        Assert.True(Guid.TryParse(identity.InstanceId, out _));
    }

    [Fact]
    public void A_dirty_tree_is_marked_in_the_version_while_the_revision_stays_the_commit()
    {
        var (version, revision) = ReleaseIdentity.ParseInformationalVersion($"1.0.0+{Sha}-dirty");

        Assert.Equal("1.0.0+c1a1c2e-dirty", version);
        Assert.Equal(Sha, revision);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0+", "1.0.0")]
    [InlineData("1.0.0+local-build", "1.0.0+local-build")]
    [InlineData("1.0.0+abc", "1.0.0+abc")] // too short to be a commit SHA
    public void A_build_without_a_sha_falls_back_without_a_revision(string? informationalVersion, string expectedVersion)
    {
        var (version, revision) = ReleaseIdentity.ParseInformationalVersion(informationalVersion);
        Assert.Equal(expectedVersion, version);
        Assert.Null(revision);

        // …and the resource is still built: no revision attribute, never an exception.
        var attributes = Attributes(ReleaseIdentity.Resolve("catalog", Config(), "Production", informationalVersion));
        Assert.Equal(expectedVersion, attributes[ReleaseIdentity.ServiceVersionKey]);
        Assert.False(attributes.ContainsKey(ReleaseIdentity.RevisionKey));
        Assert.Equal("prod", attributes[ReleaseIdentity.EnvironmentKey]);
    }

    [Theory]
    [InlineData("Development", "dev")]
    [InlineData("Staging", "test")]
    [InlineData("Test", "test")]
    [InlineData("Production", "prod")]
    [InlineData("CI", "ci")]
    [InlineData("Perf-EU", "perf-eu")]
    [InlineData(null, "unknown")]
    public void The_host_environment_maps_to_a_deployment_environment_name(string? hostEnvironment, string expected) =>
        Assert.Equal(expected, ReleaseIdentity.MapEnvironment(hostEnvironment));

    [Fact]
    public void Explicit_OTEL_RESOURCE_ATTRIBUTES_win_over_build_metadata_and_the_host_environment()
    {
        var identity = ReleaseIdentity.Resolve(
            "catalog",
            Config($"deployment.environment.name=test, service.version=2.3.4,vcs.ref.head.revision=deadbeef, service.instance.id=pod%2D7,team=core"),
            "Production",
            $"1.0.0+{Sha}");

        var attributes = Attributes(identity);
        Assert.Equal("test", attributes[ReleaseIdentity.EnvironmentKey]);
        Assert.Equal("2.3.4", attributes[ReleaseIdentity.ServiceVersionKey]);
        Assert.Equal("deadbeef", attributes[ReleaseIdentity.RevisionKey]);
        Assert.Equal("pod-7", attributes[ReleaseIdentity.InstanceIdKey]); // percent-decoded per the OTel spec
    }

    [Fact]
    public void Explicit_attributes_override_only_the_keys_they_name()
    {
        var identity = ReleaseIdentity.Resolve("catalog", Config("deployment.environment.name=test"), "Production", $"1.0.0+{Sha}");

        Assert.Equal("test", identity.Environment);
        Assert.Equal("1.0.0+c1a1c2e", identity.ServiceVersion);
        Assert.Equal(Sha, identity.Revision);
    }

    [Fact]
    public void Malformed_resource_attribute_pairs_are_skipped()
    {
        var parsed = ReleaseIdentity.ParseResourceAttributes(" ,=x,novalue,empty=,deployment.environment.name=qa ");
        Assert.Equal(new Dictionary<string, string> { ["deployment.environment.name"] = "qa" }, parsed);
    }

    [Fact]
    public void A_service_host_stamps_every_signal_with_one_release_resource()
    {
        using var host = ServiceHost("deployment.environment.name=qa", otlp: true);
        var identity = host.Services.GetRequiredService<ReleaseIdentity>();

        Assert.Equal("qa", identity.Environment); // explicit env var wins in the real host too
        foreach (var provider in new BaseProvider[]
                 {
                     host.Services.GetRequiredService<TracerProvider>(),
                     host.Services.GetRequiredService<MeterProvider>(),
                 })
        {
            var resource = provider.GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);
            Assert.Equal("release-probe", resource["service.name"]);
            Assert.Equal(identity.ServiceVersion, resource[ReleaseIdentity.ServiceVersionKey]);
            Assert.Equal("qa", resource[ReleaseIdentity.EnvironmentKey]);
            Assert.Equal(identity.InstanceId, resource[ReleaseIdentity.InstanceIdKey]);
            if (identity.Revision is not null)
                Assert.Equal(identity.Revision, resource[ReleaseIdentity.RevisionKey]);
        }
    }

    [Fact]
    public void A_service_host_without_explicit_attributes_maps_its_environment_and_reads_its_build()
    {
        using var host = ServiceHost(resourceAttributes: "", otlp: false);
        var identity = host.Services.GetRequiredService<ReleaseIdentity>();
        var resource = host.Services.GetRequiredService<TracerProvider>().GetResource().Attributes
            .ToDictionary(a => a.Key, a => a.Value);

        Assert.Equal("dev", resource[ReleaseIdentity.EnvironmentKey]);
        Assert.False(string.IsNullOrEmpty((string)resource[ReleaseIdentity.ServiceVersionKey]));
        Assert.Equal(identity.ServiceVersion, resource[ReleaseIdentity.ServiceVersionKey]);
    }

    [Fact]
    public void AddReleaseIdentity_is_idempotent_per_host()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        var first = builder.AddReleaseIdentity("release-probe");
        var second = builder.AddReleaseIdentity("release-probe");

        Assert.Same(first, second);
        Assert.Single(builder.Services, d => d.ServiceType == typeof(ReleaseIdentity));
    }

    private static IHost ServiceHost(string resourceAttributes, bool otlp)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ReleaseIdentity.ResourceAttributesSetting] = resourceAttributes,
            // An endpoint only switches exporters on; nothing is exported because the host is never started.
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = otlp ? "http://127.0.0.1:4317" : "",
        });
        builder.AddServiceTelemetry("release-probe");
        return builder.Build();
    }
}
