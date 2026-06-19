namespace ThreeCommerce.Payments.Domain;

public enum PaymentAccountState
{
    Draft = 1,
    PendingApproval = 2,
    Active = 3,
    Suspended = 4,
    Archived = 5,
}

public enum PaymentProviderMode
{
    Test = 1,
    Live = 2,
}

public class PaymentAccount
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid? StorefrontId { get; init; }
    public required string Name { get; init; }
    public required string Provider { get; init; }
    public PaymentProviderMode Mode { get; init; }
    public PaymentAccountState State { get; private set; } = PaymentAccountState.Draft;
    public bool IsDefaultForTenant { get; init; }
    public string? ExternalAccountRef { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }

    public PaymentAccountReadiness CheckReadiness()
    {
        var missing = new List<string>();
        if (TenantId == Guid.Empty)
        {
            missing.Add("tenant");
        }

        if (string.IsNullOrWhiteSpace(Provider))
        {
            missing.Add("provider");
        }

        if (Mode == PaymentProviderMode.Live && string.IsNullOrWhiteSpace(ExternalAccountRef))
        {
            missing.Add("live provider account reference");
        }

        return new PaymentAccountReadiness(missing.Count == 0, missing);
    }

    public void SubmitForApproval(DateTimeOffset now)
    {
        EnsureState(PaymentAccountState.Draft, PaymentAccountState.Suspended);
        State = PaymentAccountState.PendingApproval;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        EnsureState(PaymentAccountState.PendingApproval);
        var readiness = CheckReadiness();
        if (!readiness.IsReady)
        {
            throw new PaymentAccountRuleException($"Payment account is missing: {string.Join(", ", readiness.MissingRequirements)}.");
        }

        State = PaymentAccountState.Active;
        ActivatedAt ??= now;
        UpdatedAt = now;
    }

    public void Suspend(DateTimeOffset now)
    {
        EnsureState(PaymentAccountState.Active, PaymentAccountState.PendingApproval);
        State = PaymentAccountState.Suspended;
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        State = PaymentAccountState.Archived;
        UpdatedAt = now;
    }

    public PaymentAccountSnapshot SnapshotForCheckout(Guid storefrontId)
    {
        if (State != PaymentAccountState.Active)
        {
            throw new PaymentAccountRuleException("Checkout requires an active payment account.");
        }

        if (StorefrontId is { } scopedStorefrontId && scopedStorefrontId != storefrontId)
        {
            throw new PaymentAccountRuleException("Payment account is not eligible for this storefront.");
        }

        return new PaymentAccountSnapshot(Id, TenantId, StorefrontId, Provider, Mode, ExternalAccountRef);
    }

    private void EnsureState(params PaymentAccountState[] allowed)
    {
        if (!allowed.Contains(State))
        {
            throw new PaymentAccountRuleException($"Payment account state {State} cannot perform this transition.");
        }
    }
}

public sealed record PaymentAccountReadiness(bool IsReady, IReadOnlyList<string> MissingRequirements);

public sealed record PaymentAccountSnapshot(
    Guid PaymentAccountId,
    Guid TenantId,
    Guid? StorefrontId,
    string Provider,
    PaymentProviderMode Mode,
    string? ExternalAccountRef);

public sealed class PaymentAccountRuleException(string message) : Exception(message);
