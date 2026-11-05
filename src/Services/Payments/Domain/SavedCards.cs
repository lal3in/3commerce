namespace ThreeCommerce.Payments.Domain;

public enum SavedPaymentMethodState
{
    Active = 1,
    Removed = 2,
}

/// <summary>
/// Payments-owned customer mapping for provider vaults (Stripe Customer in v1).
/// Identity owns the user; Payments owns the sensitive provider reference.
/// </summary>
public class PaymentCustomer
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public required string Provider { get; init; }
    public required string ProviderCustomerId { get; set; }

    /// <summary>
    /// The customer's email (normalized), from the signed-in customer's claims — how a charge that knows only the customer's
    /// email (a metered usage charge: Usage balances are keyed by tenant + email) finds the customer's default saved card
    /// (owner decision 2026-11-06, <c>jobmgr_3_settlement</c>). Null on a row created before it was recorded until the
    /// customer next manages their cards (or the migration backfilled it from a subscription that charged this customer).
    /// </summary>
    public string? Email { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<SavedPaymentMethod> PaymentMethods { get; init; } = [];

    /// <summary>Record the customer's email; returns whether it changed (a new or different address).</summary>
    public bool RecordEmail(string email)
    {
        var normalized = email.Trim().ToLowerInvariant();
        if (normalized.Length == 0 || normalized == Email)
        {
            return false;
        }

        Email = normalized;
        return true;
    }
}

public class SavedPaymentMethod
{
    public Guid Id { get; init; }
    public Guid PaymentCustomerId { get; init; }
    public Guid TenantId { get; init; }
    public Guid UserId { get; init; }
    public required string Provider { get; init; }
    public required string ProviderPaymentMethodId { get; init; }
    public required string Brand { get; set; }
    public required string Last4 { get; set; }
    public int ExpMonth { get; set; }
    public int ExpYear { get; set; }
    public bool IsDefault { get; private set; }
    public SavedPaymentMethodState State { get; private set; } = SavedPaymentMethodState.Active;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void MakeDefault(DateTimeOffset now)
    {
        EnsureActive();
        IsDefault = true;
        UpdatedAt = now;
    }

    public void ClearDefault(DateTimeOffset now)
    {
        IsDefault = false;
        UpdatedAt = now;
    }

    public void Remove(DateTimeOffset now)
    {
        State = SavedPaymentMethodState.Removed;
        IsDefault = false;
        UpdatedAt = now;
    }

    public SavedPaymentMethodSnapshot SnapshotForCharge()
    {
        EnsureActive();
        return new SavedPaymentMethodSnapshot(Provider, ProviderPaymentMethodId, Brand, Last4);
    }

    private void EnsureActive()
    {
        if (State != SavedPaymentMethodState.Active)
        {
            throw new SavedPaymentMethodRuleException("Saved payment method is not active.");
        }
    }
}

public sealed record SavedPaymentMethodSnapshot(string Provider, string ProviderPaymentMethodId, string Brand, string Last4);

public sealed record SavedPaymentMethodDetails(string ProviderPaymentMethodId, string Brand, string Last4, int ExpMonth, int ExpYear);

public sealed class SavedPaymentMethodRuleException(string message) : Exception(message);
