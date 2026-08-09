namespace ThreeCommerce.Payments.Domain;

public enum MandateStatus { Pending = 1, Active = 2, Failed = 3, Revoked = 4 }

/// <summary>
/// A direct-debit mandate: the customer's standing authorization to pull funds from their bank account via
/// a <see cref="DirectDebitScheme"/> (ACH/SEPA/BACS/BECS/ACSS). Created <see cref="MandateStatus.Pending"/>
/// on setup, then <see cref="MandateStatus.Active"/> once the provider confirms the mandate (client
/// acceptance / webhook). While Active it is off-session chargeable — the rail behind a recurring
/// subscription for a non-card currency. Card data / bank details never touch us (SAQ-A); we hold only the
/// provider references.
/// </summary>
public sealed class Mandate
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public Guid PaymentCustomerId { get; init; }
    public required string Provider { get; init; }
    public DirectDebitScheme Scheme { get; init; }
    public required string Currency { get; init; }

    /// <summary>The provider's setup-intent id — the handle used to confirm/activate the mandate.</summary>
    public required string ProviderSetupIntentId { get; init; }
    public string? ProviderMandateId { get; private set; }
    public string? ProviderPaymentMethodId { get; private set; }
    public MandateStatus Status { get; private set; } = MandateStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Active with a usable payment-method ref → can be charged off-session for renewals.</summary>
    public bool IsChargeable => Status == MandateStatus.Active && !string.IsNullOrEmpty(ProviderPaymentMethodId);

    private Mandate() { }

    public static Mandate Start(
        Guid tenantId, Guid userId, Guid paymentCustomerId, string provider,
        DirectDebitScheme scheme, string currency, string setupIntentId, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            PaymentCustomerId = paymentCustomerId,
            Provider = provider,
            Scheme = scheme,
            Currency = currency.Trim().ToUpperInvariant(),
            ProviderSetupIntentId = setupIntentId,
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <summary>The provider confirmed the mandate — record its refs and make it chargeable.</summary>
    public void Activate(string providerMandateId, string providerPaymentMethodId, DateTimeOffset now)
    {
        ProviderMandateId = providerMandateId;
        ProviderPaymentMethodId = providerPaymentMethodId;
        Status = MandateStatus.Active;
        UpdatedAt = now;
    }

    public void Fail(DateTimeOffset now)
    {
        Status = MandateStatus.Failed;
        UpdatedAt = now;
    }

    public void Revoke(DateTimeOffset now)
    {
        Status = MandateStatus.Revoked;
        UpdatedAt = now;
    }
}

public sealed class MandateRuleException(string message) : Exception(message);
