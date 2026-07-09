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
    public string Name { get; private set; } = string.Empty;
    public string Provider { get; private set; } = string.Empty;
    public PaymentProviderMode Mode { get; private set; }
    public PaymentAccountState State { get; private set; } = PaymentAccountState.Draft;
    public bool IsDefaultForTenant { get; private set; }
    public string? ExternalAccountRef { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }

    /// <summary>
    /// Creates a Draft payment account. The mutable descriptor fields have private setters so the only
    /// ways to change them post-creation are the domain methods below (edit, make-default, lifecycle).
    /// </summary>
    public static PaymentAccount Create(
        Guid tenantId,
        Guid? storefrontId,
        string name,
        string provider,
        PaymentProviderMode mode,
        bool isDefaultForTenant,
        string? externalAccountRef,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            StorefrontId = storefrontId,
            Name = name.Trim(),
            Provider = provider.Trim(),
            Mode = mode,
            IsDefaultForTenant = isDefaultForTenant,
            ExternalAccountRef = string.IsNullOrWhiteSpace(externalAccountRef) ? null : externalAccountRef.Trim(),
            CreatedAt = now,
        };

    /// <summary>
    /// Edits the mutable descriptor fields. Name is always editable (unless archived); provider and mode
    /// are integration-defining, so they may only change while the account is not yet Active (suspend an
    /// Active account first). Archived accounts are immutable.
    /// </summary>
    public void UpdateDetails(string name, string provider, PaymentProviderMode mode, string? externalAccountRef, DateTimeOffset now)
    {
        if (State == PaymentAccountState.Archived)
        {
            throw new PaymentAccountRuleException("Archived payment accounts cannot be edited.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new PaymentAccountRuleException("Payment account name is required.");
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new PaymentAccountRuleException("Payment account provider is required.");
        }

        if (State == PaymentAccountState.Active && (provider.Trim() != Provider || mode != Mode))
        {
            throw new PaymentAccountRuleException("Provider and mode cannot change on an active payment account; suspend it first.");
        }

        Name = name.Trim();
        Provider = provider.Trim();
        Mode = mode;
        ExternalAccountRef = string.IsNullOrWhiteSpace(externalAccountRef) ? null : externalAccountRef.Trim();
        UpdatedAt = now;
    }

    /// <summary>Marks this account as the tenant default. Archived accounts cannot become default.</summary>
    public void SetAsDefault(DateTimeOffset now)
    {
        if (State == PaymentAccountState.Archived)
        {
            throw new PaymentAccountRuleException("Archived payment accounts cannot be made the tenant default.");
        }

        IsDefaultForTenant = true;
        UpdatedAt = now;
    }

    /// <summary>Clears the tenant-default flag (used when another account becomes default).</summary>
    public void ClearDefault(DateTimeOffset now)
    {
        if (!IsDefaultForTenant)
        {
            return;
        }

        IsDefaultForTenant = false;
        UpdatedAt = now;
    }

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
