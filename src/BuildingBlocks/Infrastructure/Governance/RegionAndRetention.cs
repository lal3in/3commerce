namespace ThreeCommerce.BuildingBlocks.Infrastructure.Governance;

/// <summary>A physical data region (mt6_11). One region initially; the list grows, tenants never move.</summary>
public enum DataRegion { AustraliaEast = 1 }

/// <summary>
/// A tenant's pinned home region (mt6_11). The GOTCHA: one physical region initially and NO cross-region
/// migration — a home region is fixed once set, so <see cref="CanMoveTo"/> is always false by design.
/// </summary>
public sealed record TenantRegion(Guid TenantId, DataRegion HomeRegion)
{
    public bool CanMoveTo(DataRegion target) => false;
}

/// <summary>Broad classes of data with different legal retention obligations (mt6_11).</summary>
public enum DataCategory { OrderRecord, LedgerEntry, AuditLog, CustomerPii, OperationalLog }

/// <summary>What happens to data once its retention window passes (mt6_11).</summary>
public enum RetentionAction { Retain, Redact, Purge }

/// <summary>A retention rule: keep the category for <see cref="RetainFor"/> (null = indefinitely), then act.</summary>
public sealed record RetentionRule(RetentionAction OnExpiry, TimeSpan? RetainFor);

/// <summary>
/// The platform retention schedule (mt6_11). Financial/audit records are retained for as long as the law
/// requires (ledger + audit are effectively indefinite and append-only — can't be purged); PII is
/// redacted (anonymized via mt6_8, keeping the row) after its window; operational logs are purged.
/// </summary>
public static class RetentionPolicy
{
    private static readonly TimeSpan SevenYears = TimeSpan.FromDays(365 * 7);

    public static readonly IReadOnlyDictionary<DataCategory, RetentionRule> Schedule = new Dictionary<DataCategory, RetentionRule>
    {
        [DataCategory.OrderRecord] = new(RetentionAction.Redact, SevenYears), // keep the order, anonymize PII
        [DataCategory.LedgerEntry] = new(RetentionAction.Retain, null),       // financial truth, indefinite
        [DataCategory.AuditLog] = new(RetentionAction.Retain, null),          // append-only, tamper-evident
        [DataCategory.CustomerPii] = new(RetentionAction.Redact, SevenYears),
        [DataCategory.OperationalLog] = new(RetentionAction.Purge, TimeSpan.FromDays(90)),
    };

    public static RetentionAction Resolve(DataCategory category, TimeSpan age)
    {
        var rule = Schedule[category];
        if (rule.RetainFor is not { } window || age < window)
        {
            return RetentionAction.Retain;
        }

        return rule.OnExpiry;
    }
}
