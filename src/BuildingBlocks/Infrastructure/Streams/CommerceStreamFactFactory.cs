using ThreeCommerce.BuildingBlocks.Contracts.Streams;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Streams;

public static class CommerceStreamFactFactory
{
    public static StreamEventEnvelope<OrderLifecycleStreamPayload> OrderLifecycle(
        Guid tenantId,
        Guid orderId,
        string status,
        long totalMinor,
        string currency,
        DateTimeOffset changedAt,
        string sourceService = "ordering",
        string? correlationId = null)
    {
        var payload = new OrderLifecycleStreamPayload(orderId, status, totalMinor, currency, changedAt);
        return StreamEventEnvelope<OrderLifecycleStreamPayload>.Create(
            Guid.CreateVersion7(),
            "OrderLifecycleChanged",
            1,
            changedAt,
            sourceService,
            tenantId,
            correlationId,
            StreamPartitionKeys.TenantAggregate(tenantId, orderId),
            StreamPrivacyClass.Internal,
            payload);
    }

    public static StreamEventEnvelope<PaymentLedgerStreamPayload> LedgerPosted(
        Guid tenantId,
        Guid journalEntryId,
        string journalType,
        long debitMinor,
        long creditMinor,
        string currency,
        DateTimeOffset postedAt,
        string referenceType,
        string referenceId,
        string sourceService = "payments",
        string? correlationId = null)
    {
        var payload = new PaymentLedgerStreamPayload(journalEntryId, journalType, debitMinor, creditMinor, currency, postedAt, referenceType, referenceId);
        return StreamEventEnvelope<PaymentLedgerStreamPayload>.Create(
            Guid.CreateVersion7(),
            "PaymentLedgerPosted",
            1,
            postedAt,
            sourceService,
            tenantId,
            correlationId,
            StreamPartitionKeys.TenantAggregate(tenantId, journalEntryId),
            StreamPrivacyClass.Internal,
            payload);
    }

    public static StreamEventEnvelope<CatalogOfferStreamPayload> OfferChanged(
        Guid tenantId,
        Guid offerId,
        Guid productId,
        Guid? variantId,
        Guid? supplierId,
        string supplyCategory,
        string fulfilmentType,
        long priceMinor,
        string currency,
        DateTimeOffset changedAt,
        string sourceService = "catalog",
        string? correlationId = null)
    {
        var payload = new CatalogOfferStreamPayload(offerId, productId, variantId, supplierId, supplyCategory, fulfilmentType, priceMinor, currency, changedAt);
        return StreamEventEnvelope<CatalogOfferStreamPayload>.Create(
            Guid.CreateVersion7(),
            "CatalogOfferChanged",
            1,
            changedAt,
            sourceService,
            tenantId,
            correlationId,
            StreamPartitionKeys.TenantAggregate(tenantId, offerId),
            StreamPrivacyClass.Internal,
            payload);
    }

    public static StreamEventEnvelope<UsageRecordStreamPayload> UsageRecorded(
        Guid tenantId,
        Guid usageRecordId,
        Guid customerId,
        string meterCode,
        long quantity,
        DateTimeOffset recordedAt,
        string referenceId,
        string sourceService = "usage",
        string? correlationId = null)
    {
        var payload = new UsageRecordStreamPayload(usageRecordId, customerId, meterCode, quantity, recordedAt, referenceId);
        return StreamEventEnvelope<UsageRecordStreamPayload>.Create(
            Guid.CreateVersion7(),
            "UsageRecorded",
            1,
            recordedAt,
            sourceService,
            tenantId,
            correlationId,
            StreamPartitionKeys.TenantAggregate(tenantId, customerId),
            StreamPrivacyClass.Internal,
            payload);
    }
}
