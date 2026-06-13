namespace ThreeCommerce.IntegrationTests;

// Testcontainers are heavy and stateful: share one fixture per kind across classes
// and run the assembly serially (avoids many concurrent Postgres/RabbitMQ containers).
[CollectionDefinition(Name)]
public sealed class Phase2Collection : ICollectionFixture<Phase2Fixture>
{
    public const string Name = "phase2";
}

[CollectionDefinition(Name)]
public sealed class SpineCollection : ICollectionFixture<SpineFixture>
{
    public const string Name = "spine";
}

[CollectionDefinition(Name)]
public sealed class Phase3Collection : ICollectionFixture<Phase3Fixture>
{
    public const string Name = "phase3";
}
