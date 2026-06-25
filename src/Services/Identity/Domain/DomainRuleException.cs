namespace ThreeCommerce.Identity.Domain;

/// <summary>Raised when a domain invariant would be violated (e.g. removing the last tenant owner).</summary>
public sealed class DomainRuleException(string message) : InvalidOperationException(message);
