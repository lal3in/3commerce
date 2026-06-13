using ThreeCommerce.BuildingBlocks.Contracts.Ping;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit-lane guard: contracts must be records with value equality — inbox dedup and
/// test assertions rely on it. Fails if someone converts a contract to a class.
/// </summary>
public class ContractTests
{
    [Fact]
    public void Contracts_have_value_equality()
    {
        var id = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow;

        Assert.Equal(new PingRequested(id, at), new PingRequested(id, at));
        Assert.Equal(new PongResponded(id, "ordering"), new PongResponded(id, "ordering"));
        Assert.NotEqual(new PongResponded(id, "ordering"), new PongResponded(id, "other"));
    }
}
