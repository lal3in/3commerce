using System.Reflection;
using System.Text.RegularExpressions;
using MassTransit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit-lane guard (ADR-0060): every RabbitMQ receive endpoint (queue) belongs to exactly ONE service.
/// All services share the broker vhost and the bus derives a queue name from the consumer / saga type name
/// alone (<see cref="MassTransitExtensions.EndpointNameFormatter"/>). Two services that both declare a
/// <c>OrderConfirmedConsumer</c> therefore bind the SAME queue and become competing consumers — each message
/// reaches only one of them. That shipped once: Fulfillment and Notifications split every <c>OrderConfirmed</c>,
/// so about half the orders were never fulfilled and the other half never got their confirmation email.
/// In-process integration fixtures give each test its own broker, so only this static check catches it.
/// </summary>
public sealed class ConsumerEndpointNameTests
{
    private static readonly IEndpointNameFormatter Formatter = MassTransitExtensions.EndpointNameFormatter;

    [Fact]
    public void Every_receive_endpoint_name_belongs_to_exactly_one_service()
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var host in BusHosts())
        {
            foreach (var (endpoint, type) in ReceiveEndpoints(host))
            {
                if (!owners.TryGetValue(endpoint, out var list))
                {
                    owners[endpoint] = list = [];
                }

                list.Add($"{host.Service}:{type.FullName}");
            }
        }

        var shared = owners
            .Where(o => o.Value.Select(v => v[..v.IndexOf(':')]).Distinct().Count() > 1)
            .Select(o => $"queue '{o.Key}' is consumed by {string.Join(" AND ", o.Value)}")
            .ToList();

        Assert.True(shared.Count == 0,
            "Receive endpoints shared across services become COMPETING consumers (each message reaches only one "
            + "service). Prefix the consumer class with its service name (e.g. FulfillmentOrderConfirmedConsumer):\n  "
            + string.Join("\n  ", shared));
        Assert.Contains("fulfillment-order-confirmed", owners.Keys);
        Assert.Contains("order-confirmed", owners.Keys);
    }

    [Fact]
    public void Every_bus_host_contributes_its_consumers_to_the_guard()
    {
        // A host whose assemblies this project cannot load would silently drop out of the uniqueness check, so
        // each one must resolve (add a ProjectReference for a new service) and the known consumers must be seen.
        var hosts = BusHosts();
        Assert.Contains(hosts, h => h.Service == "notifications");
        Assert.True(hosts.Count >= 14, $"expected every services.sh service + notifications, got {hosts.Count}");

        var byService = hosts.ToDictionary(h => h.Service, h => ReceiveEndpoints(h).Select(e => e.Endpoint).ToHashSet());
        Assert.Contains("order-confirmed", byService["notifications"]);
        Assert.Contains("fulfillment-order-confirmed", byService["fulfillment"]);
        Assert.Contains("storefront-duplicated", byService["payments"]);
        Assert.Contains("fulfillment-storefront-duplicated", byService["fulfillment"]);
        Assert.Contains("checkout-state", byService["ordering"]);
        Assert.Contains("rma-state", byService["support"]);
    }

    [Fact]
    public void BuildingBlocks_defines_no_consumers_or_sagas()
    {
        // A consumer in shared plumbing would be registered by every service that adds it — one queue, N competitors.
        foreach (var name in new[] { "3commerce.BuildingBlocks.Infrastructure", "3commerce.BuildingBlocks.Contracts" })
        {
            var endpoints = EndpointTypes(Assembly.Load(new AssemblyName(name))).Select(t => t.FullName).ToList();
            Assert.True(endpoints.Count == 0, $"{name} must not define consumers/sagas: {string.Join(", ", endpoints)}");
        }
    }

    [Fact]
    public void Endpoint_names_are_not_overridden_outside_the_guard()
    {
        // The guard derives names from type names; an explicit endpoint name (a definition, ReceiveEndpoint("…"),
        // or Endpoint(e => e.Name = …)) would bypass it. Extend this test before introducing one.
        var definitions = BusHosts().SelectMany(ServiceAssemblies).Distinct().SelectMany(LoadableTypes)
            .Where(t => !t.IsAbstract && IsSubclassOfGeneric(t, typeof(ConsumerDefinition<>))
                || !t.IsAbstract && IsSubclassOfGeneric(t, typeof(SagaDefinition<>)))
            .Select(t => t.FullName)
            .ToList();
        Assert.True(definitions.Count == 0, $"Endpoint definitions not modelled by the guard: {string.Join(", ", definitions)}");

        var root = FindRepositoryRoot();
        var overrides = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != "MassTransitExtensions.cs") // the one sanctioned AddMassTransit
            .Where(f => ExplicitEndpoint().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.True(overrides.Count == 0,
            $"Explicit endpoint names / direct AddMassTransit bypass the shared formatter: {string.Join(", ", overrides)}");
    }

    private sealed record BusHost(string Service, string RootAssembly, string OwnPrefix);

    /// <summary>The DB-owning services from <c>scripts/lib/services.sh</c> plus the Notifications worker.</summary>
    private static List<BusHost> BusHosts()
    {
        var servicesSh = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "lib", "services.sh"));
        var hosts = Regex.Matches(servicesSh, "\"([a-z]+):src/Services/([A-Za-z]+)/Api:[0-9]+\"")
            .Select(m => new BusHost(m.Groups[1].Value, $"3commerce.{m.Groups[2].Value}.Api", $"3commerce.{m.Groups[2].Value}."))
            .ToList();
        hosts.Add(new BusHost("notifications", "3commerce.Workers.Notifications", "3commerce.Workers.Notifications"));
        return hosts;
    }

    /// <summary>The host's own assemblies: its root plus every transitively referenced assembly under its prefix.</summary>
    private static IEnumerable<Assembly> ServiceAssemblies(BusHost host)
    {
        var seen = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>();
        queue.Enqueue(Assembly.Load(new AssemblyName(host.RootAssembly)));
        while (queue.Count > 0)
        {
            var assembly = queue.Dequeue();
            if (!seen.TryAdd(assembly.GetName().Name!, assembly))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name!.StartsWith(host.OwnPrefix, StringComparison.Ordinal) && !seen.ContainsKey(reference.Name))
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
            }
        }

        return seen.Values;
    }

    private static IEnumerable<(string Endpoint, Type Type)> ReceiveEndpoints(BusHost host) =>
        ServiceAssemblies(host).SelectMany(EndpointTypes).Select(t => (EndpointName(t), t));

    /// <summary>Concrete consumers (an endpoint per consumer type) and saga instances (an endpoint per saga).</summary>
    private static IEnumerable<Type> EndpointTypes(Assembly assembly) =>
        LoadableTypes(assembly).Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
            && (IsConsumer(t) || typeof(ISaga).IsAssignableFrom(t)));

    private static string EndpointName(Type type)
    {
        var method = IsConsumer(type)
            ? typeof(IEndpointNameFormatter).GetMethod(nameof(IEndpointNameFormatter.Consumer))!
            : typeof(IEndpointNameFormatter).GetMethod(nameof(IEndpointNameFormatter.Saga))!;
        return (string)method.MakeGenericMethod(type).Invoke(Formatter, null)!;
    }

    private static bool IsConsumer(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>));

    private static bool IsSubclassOfGeneric(Type type, Type generic)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == generic)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "3commerce.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (3commerce.sln) not found.");
    }

    /// <summary>
    /// <c>ReceiveEndpoint("…")</c>, <c>e.Name = …</c> inside an <c>.Endpoint(…)</c>, or a bus built outside
    /// <c>AddServiceBus</c> (which would not use the shared formatter).
    /// </summary>
    private static Regex ExplicitEndpoint() => new(
        @"\.ReceiveEndpoint\(\s*""|\.Endpoint\([^;]*\.Name\s*=|\.AddMassTransit\(",
        RegexOptions.Compiled);
}
