using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Payments.Domain;

public enum SubscriptionStatus { Trialing = 1, Active = 2, PastDue = 3, Cancelled = 4 }

/// <summary>
/// A recurring billing arrangement for a subscription line (Phase 7 / mt7_3). The first period is
/// paid with the order at checkout; renewals charge <see cref="PriceMinor"/> via IPaymentProvider and
/// advance the period. Lives in Payments (the rail/ledger owner) for now.
/// </summary>
public sealed class Subscription
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid OrderId { get; init; }
    public required string CustomerEmail { get; init; }
    public Guid ProductId { get; init; }
    public Guid? VariantId { get; init; }
    public BillingPeriod BillingPeriod { get; init; }
    public long PriceMinor { get; init; }
    public required string Currency { get; init; }
    public SubscriptionStatus Status { get; private set; } = SubscriptionStatus.Active;
    public DateTimeOffset CurrentPeriodStart { get; private set; }
    public DateTimeOffset CurrentPeriodEnd { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing;

    private Subscription() { }

    public static Subscription Start(
        Guid tenantId, Guid orderId, string email, Guid productId, Guid? variantId,
        BillingPeriod period, long priceMinor, string currency, DateTimeOffset now)
    {
        if (period == BillingPeriod.Once)
        {
            throw new SubscriptionRuleException("A subscription needs a recurring billing period.");
        }

        return new Subscription
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrderId = orderId,
            CustomerEmail = email.Trim().ToLowerInvariant(),
            ProductId = productId,
            VariantId = variantId,
            BillingPeriod = period,
            PriceMinor = priceMinor,
            Currency = currency.ToUpperInvariant(),
            CurrentPeriodStart = now,
            CurrentPeriodEnd = Advance(now, period),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Advance to the next period after a successful renewal charge.</summary>
    public void Renew(DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Cancelled)
        {
            throw new SubscriptionRuleException("Cannot renew a cancelled subscription.");
        }

        CurrentPeriodStart = CurrentPeriodEnd;
        CurrentPeriodEnd = Advance(CurrentPeriodEnd, BillingPeriod);
        Status = SubscriptionStatus.Active;
        UpdatedAt = now;
    }

    public void MarkPastDue(DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.Cancelled)
        {
            Status = SubscriptionStatus.PastDue;
            UpdatedAt = now;
        }
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = SubscriptionStatus.Cancelled;
        UpdatedAt = now;
    }

    private static DateTimeOffset Advance(DateTimeOffset from, BillingPeriod period) => period switch
    {
        BillingPeriod.Monthly => from.AddMonths(1),
        BillingPeriod.Yearly => from.AddYears(1),
        _ => from,
    };
}

public sealed class SubscriptionRuleException(string message) : Exception(message);
