namespace ThreeCommerce.BuildingBlocks.Contracts.Streams;

public sealed record OrderLifecycleStreamPayload(
    Guid OrderId,
    string Status,
    long TotalMinor,
    string Currency,
    DateTimeOffset ChangedAt);

public sealed record PaymentLedgerStreamPayload(
    Guid JournalEntryId,
    string JournalType,
    long DebitMinor,
    long CreditMinor,
    string Currency,
    DateTimeOffset PostedAt,
    string ReferenceType,
    string ReferenceId);

public sealed record CatalogOfferStreamPayload(
    Guid OfferId,
    Guid ProductId,
    Guid? VariantId,
    Guid? SupplierId,
    string SupplyCategory,
    string FulfilmentType,
    long PriceMinor,
    string Currency,
    DateTimeOffset ChangedAt);

public sealed record UsageRecordStreamPayload(
    Guid UsageRecordId,
    Guid CustomerId,
    string MeterCode,
    long Quantity,
    DateTimeOffset RecordedAt,
    string ReferenceId);
