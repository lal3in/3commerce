namespace ThreeCommerce.BuildingBlocks.Contracts.Streams;

/// <summary>Initial durable event-stream topic catalog from ADR-0034.</summary>
public static class StreamTopics
{
    public const string AuditEntries = "audit.entries";
    public const string CommerceOrders = "commerce.orders";
    public const string PaymentsLedger = "payments.ledger";
    public const string CatalogOffers = "catalog.offers";
    public const string UsageRecords = "usage.records";
    public const string MarketingEvents = "marketing.events";
    public const string WorkflowRuns = "workflow.runs";
    public const string WebhookDeliveries = "webhook.deliveries";
}
