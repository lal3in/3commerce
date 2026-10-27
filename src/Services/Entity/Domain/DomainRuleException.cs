namespace ThreeCommerce.Entity.Domain;

/// <summary>
/// A domain invariant was violated. <see cref="Field"/> optionally names the request field at fault so
/// an endpoint can return a 400 problem+json whose <c>errors</c> key is that field (ADR-0063 P2:
/// "invalid → 400 naming the field"); null leaves the choice of key to the endpoint.
/// </summary>
public sealed class DomainRuleException(string message, string? field = null) : InvalidOperationException(message)
{
    public string? Field { get; } = field;
}
