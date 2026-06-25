using System.Runtime.CompilerServices;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Integration tests boot many real service hosts in one process. Quartz's logging is a process-global
/// static (<c>Quartz.Logging.LogProvider</c>); the in-memory scheduler (Ordering saga timeout) and the
/// mt6_3 job scheduler tie it to one host's ILoggerFactory, which is then disposed when that host is —
/// poisoning the next host's scheduler start with an ObjectDisposedException. Disabling Quartz's global
/// logging here (it is noise in tests) severs that tie so schedulers start cleanly across hosts.
/// </summary>
internal static class QuartzTestLogging
{
    [ModuleInitializer]
    internal static void Disable() => Quartz.Logging.LogProvider.IsDisabled = true;
}
