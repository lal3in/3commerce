namespace ThreeCommerce.Catalog.Domain;

public sealed class CatalogRuleException(string message) : InvalidOperationException(message);
