using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Domain;

public enum SupplierBankAccountState
{
    PendingApproval = 1,
    Active = 2,
    Rejected = 3,
    Archived = 4,
}

public enum PayoutCadence
{
    Manual = 1,
    Weekly = 2,
    Fortnightly = 3,
    Monthly = 4,
}

public class SupplierBankAccount
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid SupplierEntityId { get; init; }
    public string AccountName { get; private set; } = string.Empty;
    public string BankCountry { get; private set; } = string.Empty;
    public string RoutingNumberMasked { get; private set; } = string.Empty;
    public string AccountNumberMasked { get; private set; } = string.Empty;
    public string AccountTokenRef { get; private set; } = string.Empty;
    public SupplierBankAccountState State { get; private set; } = SupplierBankAccountState.PendingApproval;
    public string? ApprovalReason { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>
    /// Creates a pending-approval bank account from masked display values plus a vault token reference.
    /// Raw bank details never enter this service; the setters are private so the only post-creation
    /// change path is <see cref="UpdateDetails"/> (which re-triggers approval on identity changes).
    /// </summary>
    public static SupplierBankAccount Create(
        Guid tenantId,
        Guid supplierEntityId,
        string accountName,
        string bankCountry,
        string routingNumberMasked,
        string accountNumberMasked,
        string accountTokenRef,
        DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SupplierEntityId = supplierEntityId,
            AccountName = accountName.Trim(),
            BankCountry = bankCountry.Trim().ToUpperInvariant(),
            RoutingNumberMasked = routingNumberMasked.Trim(),
            AccountNumberMasked = accountNumberMasked.Trim(),
            AccountTokenRef = accountTokenRef.Trim(),
            CreatedAt = now,
        };

    /// <summary>
    /// Edits masked/label fields. The account name is a cosmetic label; changing the banking identity
    /// (country, masked routing/account, or vault token) means a different underlying account, so an
    /// approved or rejected account is reset to PendingApproval and must be re-approved. Archived
    /// accounts are immutable. Raw bank details are never accepted or stored.
    /// </summary>
    public void UpdateDetails(string accountName, string bankCountry, string routingNumberMasked, string accountNumberMasked, string accountTokenRef)
    {
        if (State == SupplierBankAccountState.Archived)
        {
            throw new SupplierPayableRuleException("Archived bank accounts cannot be edited.");
        }

        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new SupplierPayableRuleException("Bank account name is required.");
        }

        var normalizedCountry = bankCountry.Trim().ToUpperInvariant();
        var normalizedRouting = routingNumberMasked.Trim();
        var normalizedAccount = accountNumberMasked.Trim();
        var normalizedToken = accountTokenRef.Trim();

        var identityChanged =
            !string.Equals(BankCountry, normalizedCountry, StringComparison.Ordinal) ||
            !string.Equals(RoutingNumberMasked, normalizedRouting, StringComparison.Ordinal) ||
            !string.Equals(AccountNumberMasked, normalizedAccount, StringComparison.Ordinal) ||
            !string.Equals(AccountTokenRef, normalizedToken, StringComparison.Ordinal);

        AccountName = accountName.Trim();
        BankCountry = normalizedCountry;
        RoutingNumberMasked = normalizedRouting;
        AccountNumberMasked = normalizedAccount;
        AccountTokenRef = normalizedToken;

        if (identityChanged && State is SupplierBankAccountState.Active or SupplierBankAccountState.Rejected)
        {
            State = SupplierBankAccountState.PendingApproval;
            ApprovalReason = null;
            ApprovedAt = null;
        }
    }

    public void Approve(string reason, DateTimeOffset now)
    {
        if (State != SupplierBankAccountState.PendingApproval)
        {
            throw new SupplierPayableRuleException("Only pending bank accounts can be approved.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new SupplierPayableRuleException("Bank account approval requires a reason.");
        }

        State = SupplierBankAccountState.Active;
        ApprovalReason = reason.Trim();
        ApprovedAt = now;
    }

    public void Reject(string reason)
    {
        if (State != SupplierBankAccountState.PendingApproval)
        {
            throw new SupplierPayableRuleException("Only pending bank accounts can be rejected.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new SupplierPayableRuleException("Bank account rejection requires a reason.");
        }

        State = SupplierBankAccountState.Rejected;
        ApprovalReason = reason.Trim();
    }

    public void EnsureActive()
    {
        if (State != SupplierBankAccountState.Active)
        {
            throw new SupplierPayableRuleException("Payout instructions require an approved active bank account.");
        }
    }

    public void Archive()
    {
        if (State == SupplierBankAccountState.Archived)
        {
            throw new SupplierPayableRuleException("Bank account is already archived.");
        }

        State = SupplierBankAccountState.Archived;
    }
}

public class PayoutInstruction
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid SupplierEntityId { get; init; }
    public Guid BankAccountId { get; private set; }
    public PayoutCadence Cadence { get; private set; }
    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; init; }

    public static PayoutInstruction Create(SupplierBankAccount bankAccount, PayoutCadence cadence, DateTimeOffset now)
    {
        bankAccount.EnsureActive();
        return new PayoutInstruction
        {
            Id = Guid.CreateVersion7(),
            TenantId = bankAccount.TenantId,
            SupplierEntityId = bankAccount.SupplierEntityId,
            BankAccountId = bankAccount.Id,
            Cadence = cadence,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Re-points the instruction at a (possibly different) bank account and/or changes its cadence
    /// (schedule). The target bank account must be Active — the same guard as creation — so a payout
    /// instruction can never route to an unapproved account. Deactivated instructions can be re-edited;
    /// this does not, by itself, reactivate them.
    /// </summary>
    public void Update(SupplierBankAccount bankAccount, PayoutCadence cadence)
    {
        bankAccount.EnsureActive();
        if (bankAccount.TenantId != TenantId || bankAccount.SupplierEntityId != SupplierEntityId)
        {
            throw new SupplierPayableRuleException("Payout instruction bank account must belong to the same tenant and supplier.");
        }

        BankAccountId = bankAccount.Id;
        Cadence = cadence;
    }

    public void Deactivate() => Active = false;
}

public class SupplierPayablePolicy
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid SupplierEntityId { get; init; }
    public int CommissionBps { get; init; }
    public PayoutCadence Cadence { get; init; }
    public bool Active { get; init; } = true;

    public long CalculateSupplierNet(long grossMinor)
    {
        if (!Active)
        {
            throw new SupplierPayableRuleException("Supplier payable policy is inactive.");
        }

        if (grossMinor < 0 || CommissionBps is < 0 or > 10_000)
        {
            throw new SupplierPayableRuleException("Supplier payable policy values are invalid.");
        }

        return grossMinor - grossMinor * CommissionBps / 10_000;
    }
}

public class SupplierPayable
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid SupplierEntityId { get; init; }
    public Guid OrderId { get; init; }
    public long GrossMinor { get; init; }
    public long CommissionMinor { get; init; }
    public long NetPayableMinor { get; init; }
    public required string Currency { get; init; }
    public DateTimeOffset CreatedAt { get; init; }

    public static SupplierPayable Create(Guid tenantId, Guid supplierEntityId, Guid orderId, long grossMinor, string currency, SupplierPayablePolicy policy, DateTimeOffset now)
    {
        if (policy.TenantId != tenantId || policy.SupplierEntityId != supplierEntityId)
        {
            throw new SupplierPayableRuleException("Supplier payable policy must match tenant and supplier.");
        }

        var net = policy.CalculateSupplierNet(grossMinor);
        return new SupplierPayable
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SupplierEntityId = supplierEntityId,
            OrderId = orderId,
            GrossMinor = grossMinor,
            CommissionMinor = grossMinor - net,
            NetPayableMinor = net,
            Currency = currency,
            CreatedAt = now,
        };
    }

    public JournalEntry ToAccrualEntry(DateTimeOffset now)
    {
        var entry = new JournalEntry
        {
            Id = Guid.CreateVersion7(),
            Description = $"Supplier payable {Id} for order {OrderId}",
            Reference = Id.ToString(),
            Currency = Currency,
            CreatedAt = now,
        };
        entry.Lines.Add(new JournalLine { Id = Guid.CreateVersion7(), EntryId = entry.Id, AccountCode = Accounts.CostOfGoodsSold, DebitMinor = NetPayableMinor, CreditMinor = 0 });
        entry.Lines.Add(new JournalLine { Id = Guid.CreateVersion7(), EntryId = entry.Id, AccountCode = Accounts.LiabilitySupplierPayable, DebitMinor = 0, CreditMinor = NetPayableMinor });
        return entry;
    }
}

public sealed class SupplierPayableRuleException(string message) : Exception(message);
