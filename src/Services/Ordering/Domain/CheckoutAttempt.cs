using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Ordering.Domain;

public enum CheckoutAttemptStatus
{
    AwaitingPayment = 1,
    Confirmed = 2,
    Cancelled = 3,
}

public class CheckoutAttempt
{
    public Guid Id { get; init; }
    public Guid? UserId { get; init; }
    public Guid TenantId { get; init; }
    public Guid StorefrontId { get; init; }
    public required string Email { get; init; }
    public CheckoutAttemptStatus Status { get; set; } = CheckoutAttemptStatus.AwaitingPayment;
    public long NetMinor { get; init; }
    public long ShippingMinor { get; init; }
    public long TaxMinor { get; init; }
    public long DiscountMinor { get; init; }

    /// <summary>The promotion share of <see cref="DiscountMinor"/> (ADR-0051); the remainder is the
    /// storefront-wide discount, which is a store setting rather than a promotion.</summary>
    public long PromotionDiscountMinor { get; init; }

    /// <summary>Comma-joined ids of every promotion that applied, or null when none did — the audit trail
    /// for why this charge was what it was. Combinable promotions stack, so there may be several.</summary>
    public string? AppliedPromotionIds { get; init; }

    /// <summary>
    /// The NAMES of those promotions as they read at the moment of sale, comma-joined and index-aligned
    /// with <see cref="AppliedPromotionIds"/>. An id alone stops explaining a historical charge the moment
    /// the promotion is renamed, re-aimed or deleted — and a deleted promotion leaves an order nobody can
    /// account for at all. Null for an order with no promotion.
    /// </summary>
    public string? AppliedPromotionNames { get; init; }

    /// <summary>Whether a promotion zeroed the shipping charge on this attempt.</summary>
    public bool FreeShippingApplied { get; init; }

    /// <summary>The coupon code the shopper redeemed (canonical UPPERCASE), or null when none was used
    /// (ADR-0052). Snapshotted so a past checkout can be explained — and supported — without re-running the
    /// evaluator or joining the redemption table.</summary>
    public string? CouponCode { get; init; }

    public long GrossMinor { get; init; }
    public required string Currency { get; init; }
    public required string PaymentIntentId { get; init; }
    public string PaymentOption { get; init; } = "CreditCard";
    public string? PaymentInstrumentSummary { get; init; }
    public string PaymentProvider { get; init; } = "Stripe";
    public string? CampaignRef { get; init; }
    public required string ShipName { get; init; }
    public required string ShipLine1 { get; init; }
    public required string ShipCity { get; init; }
    public string? ShipRegion { get; init; }
    public required string ShipPostcode { get; init; }
    public required string ShipCountry { get; init; }

    /// <summary>The shopper chose to collect at the fulfilling supplier's warehouse (zero shipping, no carrier).</summary>
    public bool CollectAtWarehouse { get; init; }
    public string? WarehouseName { get; init; }
    public string? WarehouseLine1 { get; init; }
    public string? WarehouseCity { get; init; }
    public string? WarehousePostcode { get; init; }
    public string? WarehouseCountry { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public List<CheckoutAttemptLine> Lines { get; init; } = [];

    public Order ToOrder(long orderNumber, DateTimeOffset now)
    {
        if (Status != CheckoutAttemptStatus.AwaitingPayment)
        {
            throw new OrderingRuleException("Only awaiting-payment checkout attempts can become orders.");
        }

        return new Order
        {
            Id = Id,
            PublicOrderNumber = orderNumber,
            TenantId = TenantId,
            StorefrontId = StorefrontId,
            UserId = UserId,
            Email = Email,
            Status = OrderStatus.Confirmed,
            NetMinor = NetMinor,
            ShippingMinor = ShippingMinor,
            TaxMinor = TaxMinor,
            DiscountMinor = DiscountMinor,
            PromotionDiscountMinor = PromotionDiscountMinor,
            AppliedPromotionIds = AppliedPromotionIds,
            AppliedPromotionNames = AppliedPromotionNames,
            FreeShippingApplied = FreeShippingApplied,
            CouponCode = CouponCode,
            GrossMinor = GrossMinor,
            Currency = Currency,
            PaymentIntentId = PaymentIntentId,
            PaymentOption = PaymentOption,
            PaymentInstrumentSummary = PaymentInstrumentSummary,
            PaymentProvider = PaymentProvider,
            ShipName = ShipName,
            ShipLine1 = ShipLine1,
            ShipCity = ShipCity,
            ShipRegion = ShipRegion,
            ShipPostcode = ShipPostcode,
            ShipCountry = ShipCountry,
            CollectAtWarehouse = CollectAtWarehouse,
            WarehouseName = WarehouseName,
            WarehouseLine1 = WarehouseLine1,
            WarehouseCity = WarehouseCity,
            WarehousePostcode = WarehousePostcode,
            WarehouseCountry = WarehouseCountry,
            CreatedAt = now,
            Lines = Lines.Select(l => new OrderLine
            {
                Id = Guid.CreateVersion7(),
                OrderId = Id,
                ProductId = l.ProductId,
                VariantId = l.VariantId,
                VariantSku = l.VariantSku,
                Title = l.Title,
                UnitPriceMinor = l.UnitPriceMinor,
                DiscountMinor = l.DiscountMinor,
                RenewalDiscountMinor = l.RenewalDiscountMinor,
                Quantity = l.Quantity,
                FulfilmentType = l.FulfilmentType,
                SupplierId = l.SupplierId,
                BillingMode = l.BillingMode,
                BillingPeriod = l.BillingPeriod,
                SupplierUnitCostMinor = l.SupplierUnitCostMinor,
            }).ToList(),
        };
    }
}

public class CheckoutAttemptLine
{
    public Guid Id { get; init; }
    public Guid CheckoutAttemptId { get; init; }
    public Guid ProductId { get; init; }
    public Guid? VariantId { get; init; }
    public string? VariantSku { get; init; }
    public required string Title { get; init; }
    public long UnitPriceMinor { get; init; }
    public long DiscountMinor { get; init; }

    /// <summary>
    /// The part of <see cref="DiscountMinor"/> that rides this line's RENEWALS when it is a subscription
    /// (ADR-0057) — 0 for an introductory promotion, for the storefront-wide discount, and for every
    /// one-time line. A renewal is charged <c>UnitPriceMinor − this</c>.
    /// </summary>
    public long RenewalDiscountMinor { get; init; }

    public int Quantity { get; init; }
    public FulfilmentType FulfilmentType { get; init; } = FulfilmentType.Unassigned;
    public Guid? SupplierId { get; init; }
    public BillingMode BillingMode { get; init; } = BillingMode.OneTime;
    public BillingPeriod BillingPeriod { get; init; } = BillingPeriod.Once;

    /// <summary>
    /// Per-unit supplier cost (COGS) of the SAME offer that set <see cref="SupplierId"/>,
    /// <see cref="FulfilmentType"/>, <see cref="BillingMode"/> and — when an offer set it — the line's price
    /// (<c>OfferResolution.ResolveLineOffer</c>), in the order's currency by construction. Snapshotted here so the cost accrued at confirmation can
    /// never come from a different offer than the supplier the line was sold with. 0 = no supply offer / unknown.
    /// </summary>
    public long SupplierUnitCostMinor { get; init; }
}

/// <summary>
/// A storefront's order-number counter: <see cref="NextNumber"/> is the number the next order gets.
/// It is advanced only by Infrastructure's <c>OrderNumberAllocator</c>, in one atomic statement outside the
/// confirming transaction — never read-then-incremented through EF, which collided (40001/23505) when two
/// orders of one storefront confirmed together. Numbers can therefore have gaps (a confirmation that rolls
/// back burns its number); they are order references, not tax-invoice numbers.
/// </summary>
public class OrderNumberSequence
{
    /// <summary>A storefront's first order number.</summary>
    public const long FirstNumber = 1000;

    public Guid StorefrontId { get; init; }
    public long NextNumber { get; set; } = FirstNumber;
}

public sealed class OrderingRuleException(string message) : Exception(message);
