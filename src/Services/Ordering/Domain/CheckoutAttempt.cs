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

    /// <summary>Whether a promotion zeroed the shipping charge on this attempt.</summary>
    public bool FreeShippingApplied { get; init; }

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
            FreeShippingApplied = FreeShippingApplied,
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
                Quantity = l.Quantity,
                FulfilmentType = l.FulfilmentType,
                SupplierId = l.SupplierId,
                BillingMode = l.BillingMode,
                BillingPeriod = l.BillingPeriod,
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
    public int Quantity { get; init; }
    public FulfilmentType FulfilmentType { get; init; } = FulfilmentType.Unassigned;
    public Guid? SupplierId { get; init; }
    public BillingMode BillingMode { get; init; } = BillingMode.OneTime;
    public BillingPeriod BillingPeriod { get; init; } = BillingPeriod.Once;
}

public class OrderNumberSequence
{
    public Guid StorefrontId { get; init; }
    public long NextNumber { get; set; } = 1000;

    public long ReserveNext()
    {
        var number = NextNumber;
        NextNumber++;
        return number;
    }
}

public sealed class OrderingRuleException(string message) : Exception(message);
