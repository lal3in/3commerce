using Microsoft.Extensions.Configuration;

namespace ThreeCommerce.BuildingBlocks.Observability;

/// <summary>
/// Continuous profiling (ADR-0068): the Pyroscope .NET profiler is a NATIVE CLR profiler that every .NET image carries
/// (see the Dockerfiles) and that the runtime loads only when the deployment sets <c>CORECLR_ENABLE_PROFILING=1</c>
/// (plus <c>LD_PRELOAD</c> of its API wrapper and <c>PYROSCOPE_PROFILING_ENABLED=true</c>) — Helm
/// <c>profiling.enabled</c>, <c>deploy/.env.dev</c>. It is Linux-only (x86_64/aarch64): bare-run services on macOS are
/// never profiled. When it is loaded, <see cref="TelemetryExtensions.AddTelemetryCore{TBuilder}"/> adds the
/// <c>PyroscopeSpanProcessor</c>: root spans get <c>pyroscope.profile.id</c> and the profiler labels their CPU samples,
/// so Grafana's Tempo → Pyroscope link opens the profile of exactly that request.
/// </summary>
public static class Profiling
{
    /// <summary>The Pyroscope profiler's own switch (read by the native profiler too).</summary>
    public const string EnabledSetting = "PYROSCOPE_PROFILING_ENABLED";

    /// <summary>The CLR switch that loads a native profiler at all.</summary>
    public const string ClrProfilingSetting = "CORECLR_ENABLE_PROFILING";

    /// <summary>
    /// True only when the native profiler is actually loaded and enabled: the span processor calls into it, so it must
    /// never be added where it is absent (bare-run on macOS, an image run without the profiling env).
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration) => IsEnabled(configuration, OperatingSystem.IsLinux());

    /// <summary><see cref="IsEnabled(IConfiguration)"/> with the platform made explicit (unit tests).</summary>
    public static bool IsEnabled(IConfiguration configuration, bool isLinux) =>
        isLinux
        && configuration[ClrProfilingSetting] == "1"
        && (string.Equals(configuration[EnabledSetting], "true", StringComparison.OrdinalIgnoreCase)
            || configuration[EnabledSetting] == "1");
}
