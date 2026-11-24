using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using ThreeCommerce.BuildingBlocks.Observability;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Continuous profiling (ADR-0068): the span processor is added only where the native Pyroscope profiler is loaded
/// (Linux + CORECLR_ENABLE_PROFILING=1 + PYROSCOPE_PROFILING_ENABLED), and every .NET image carries the same pinned,
/// checksum-verified profiler, inert by default, named after its OpenTelemetry service.name (so Tempo spans link to
/// its profiles). Unit lane — reads the repository's Dockerfiles, no Docker.
/// </summary>
public sealed partial class ProfilingTests
{
    private static IConfiguration Config(string? clr, string? pyroscope) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [Profiling.ClrProfilingSetting] = clr,
            [Profiling.EnabledSetting] = pyroscope,
        }).Build();

    [Theory]
    [InlineData(true, "1", "true", true)]
    [InlineData(true, "1", "1", true)]
    [InlineData(true, "1", "TRUE", true)]
    [InlineData(true, "1", "false", false)]
    [InlineData(true, "1", null, false)]
    [InlineData(true, "0", "true", false)]
    [InlineData(true, null, "true", false)]
    [InlineData(false, "1", "true", false)] // macOS / Windows bare-run: never, the profiler is Linux-only
    public void Span_profiles_only_where_the_native_profiler_is_loaded(bool linux, string? clr, string? pyroscope, bool expected) =>
        Assert.Equal(expected, Profiling.IsEnabled(Config(clr, pyroscope), linux));

    /// <summary>Every .NET image and the OpenTelemetry service.name its Program.cs registers.</summary>
    public static TheoryData<string, string> DotnetImages() => new()
    {
        { "src/Services/Identity/Api/Dockerfile", "identity" },
        { "src/Services/Catalog/Api/Dockerfile", "catalog" },
        { "src/Services/Entity/Api/Dockerfile", "entity" },
        { "src/Services/Ordering/Api/Dockerfile", "ordering" },
        { "src/Services/Payments/Api/Dockerfile", "payments" },
        { "src/Services/Fulfillment/Api/Dockerfile", "fulfillment" },
        { "src/Services/Support/Api/Dockerfile", "support" },
        { "src/Services/Marketing/Api/Dockerfile", "marketing" },
        { "src/Services/Pricing/Api/Dockerfile", "pricing" },
        { "src/Services/Audit/Api/Dockerfile", "audit" },
        { "src/Services/Workflow/Api/Dockerfile", "workflow" },
        { "src/Services/Entitlement/Api/Dockerfile", "entitlement" },
        { "src/Services/Usage/Api/Dockerfile", "usage" },
        { "src/Workers/Notifications/Dockerfile", "notifications" },
        { "src/Gateway/Dockerfile", "gateway" },
        { "src/Admin/Dockerfile", "admin" },
        { "src/SupplierPortal/Dockerfile", "supplier-portal" },
    };

    [Theory]
    [MemberData(nameof(DotnetImages))]
    public void Every_dotnet_image_carries_the_pinned_profiler_inert_and_named_after_its_service(string dockerfile, string serviceName)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), dockerfile));

        // The same profiler stage everywhere (byte-identical, so the copied layer is shared and one bump updates all).
        Assert.Equal(ProfilerStage(File.ReadAllText(Path.Combine(RepoRoot(), "src/Services/Catalog/Api/Dockerfile"))), ProfilerStage(text));
        Assert.Contains("sha256sum -c -", text);
        Assert.Contains("COPY --from=profiler /pyroscope /pyroscope", text);
        Assert.Contains("CORECLR_PROFILER={BD1A650D-AC5D-4896-B64F-D6FA25D6B26A}", text);
        Assert.Contains("CORECLR_PROFILER_PATH=/pyroscope/Pyroscope.Profiler.Native.so", text);

        // Inert by default: the IMAGE never turns the profiler on — the deployment does.
        Assert.DoesNotMatch(@"(?m)^\s*ENV\b.*CORECLR_ENABLE_PROFILING", text);
        Assert.DoesNotMatch(@"(?m)^\s*ENV\b.*LD_PRELOAD", text);

        Assert.Contains($"ENV PYROSCOPE_APPLICATION_NAME={serviceName}\n", text.Replace("\r\n", "\n"));
        var program = Directory.GetFiles(Path.Combine(RepoRoot(), Path.GetDirectoryName(dockerfile)!), "Program.cs").Single();
        Assert.Matches($"Add(Service|BlazorServer)Telemetry\\(\"{Regex.Escape(serviceName)}\"", File.ReadAllText(program));
    }

    private static string ProfilerStage(string dockerfile)
    {
        var match = StageRegex().Match(dockerfile.Replace("\r\n", "\n"));
        Assert.True(match.Success, "no `FROM … AS profiler` stage");
        return match.Value;
    }

    [GeneratedRegex(@"FROM mcr\.microsoft\.com/dotnet/sdk:10\.0 AS profiler\n(.+\n)+?(?=\n)")]
    private static partial Regex StageRegex();

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "3commerce.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (3commerce.sln) not found.");
    }
}
