using System.Text.Json;

namespace ThreeCommerce.IntegrationTests;

public sealed class GatewayConfigTests
{
    [Fact]
    public void Gateway_clusters_have_production_health_load_balancing_and_timeout_policy()
    {
        using var json = LoadGatewayJson("appsettings.json");
        var clusters = json.RootElement.GetProperty("ReverseProxy").GetProperty("Clusters");

        foreach (var cluster in clusters.EnumerateObject())
        {
            Assert.Equal("PowerOfTwoChoices", cluster.Value.GetProperty("LoadBalancingPolicy").GetString());

            var activeHealth = cluster.Value.GetProperty("HealthCheck").GetProperty("Active");
            Assert.True(activeHealth.GetProperty("Enabled").GetBoolean());
            Assert.Equal("/health/ready", activeHealth.GetProperty("Path").GetString());
            Assert.Equal("ConsecutiveFailures", activeHealth.GetProperty("Policy").GetString());
            Assert.Equal("00:00:10", activeHealth.GetProperty("Interval").GetString());
            Assert.Equal("00:00:03", activeHealth.GetProperty("Timeout").GetString());

            Assert.Equal("00:00:30", cluster.Value.GetProperty("HttpRequest").GetProperty("ActivityTimeout").GetString());
        }
    }

    [Fact]
    public void Container_gateway_config_preserves_the_same_cluster_policy_conventions()
    {
        using var baseJson = LoadGatewayJson("appsettings.json");
        using var containerJson = LoadGatewayJson("appsettings.Container.json");
        var baseClusters = baseJson.RootElement.GetProperty("ReverseProxy").GetProperty("Clusters");
        var containerClusters = containerJson.RootElement.GetProperty("ReverseProxy").GetProperty("Clusters");

        Assert.Equal(baseClusters.EnumerateObject().Select(c => c.Name).Order(), containerClusters.EnumerateObject().Select(c => c.Name).Order());

        foreach (var cluster in containerClusters.EnumerateObject())
        {
            Assert.Equal("PowerOfTwoChoices", cluster.Value.GetProperty("LoadBalancingPolicy").GetString());
            Assert.Equal("/health/ready", cluster.Value.GetProperty("HealthCheck").GetProperty("Active").GetProperty("Path").GetString());
            Assert.Equal("00:00:30", cluster.Value.GetProperty("HttpRequest").GetProperty("ActivityTimeout").GetString());
        }
    }

    [Fact]
    public void Gateway_program_keeps_service_health_endpoints_internal_only()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "Gateway", "Program.cs"));

        Assert.Contains("Service health endpoints are internal-only", program);
        Assert.Contains("path.StartsWith(\"/api/\"", program);
        Assert.Contains("path.Contains(\"/health\"", program);
        Assert.Contains("StatusCodes.Status404NotFound", program);
    }

    private static JsonDocument LoadGatewayJson(string fileName)
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "Gateway", fileName);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "3commerce.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
