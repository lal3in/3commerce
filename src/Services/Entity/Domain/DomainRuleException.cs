namespace ThreeCommerce.Entity.Domain;

public sealed class DomainRuleException(string message) : InvalidOperationException(message);
