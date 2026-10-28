namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// A catalog invariant was violated. <see cref="Field"/> optionally names the request field at fault so an
/// endpoint's 400 problem+json can key its <c>errors</c> by that field; null leaves the key to the endpoint.
/// </summary>
public sealed class CatalogRuleException(string message, string? field = null) : InvalidOperationException(message)
{
    public string? Field { get; } = field;
}
