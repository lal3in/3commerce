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
    public long GrossMinor { get; init; }
    public required string Currency { get; init; }
    public required string PaymentIntentId { get; init; }
    public string? CampaignRef { get; init; }
    public required string ShipName { get; init; }
    public required string ShipLine1 { get; init; }
    public required string ShipCity { get; init; }
    public required string ShipPostcode { get; init; }
    public required string ShipCountry { get; init; }
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
            GrossMinor = GrossMinor,
            Currency = Currency,
            PaymentIntentId = PaymentIntentId,
            ShipName = ShipName,
            ShipLine1 = ShipLine1,
            ShipCity = ShipCity,
            ShipPostcode = ShipPostcode,
            ShipCountry = ShipCountry,
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
