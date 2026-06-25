namespace ThreeCommerce.Fulfillment.Domain;

/// <summary>Raised when a fulfillment domain invariant would be violated.</summary>
public sealed class FulfillmentRuleException(string message) : InvalidOperationException(message);
