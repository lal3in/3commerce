namespace ThreeCommerce.Tools.OrderConfirmedBackfill.SupportOwners;

/// <summary>What Ordering knows about the order behind an ownerless Support snapshot.</summary>
public sealed record OrderOwnerFact(Guid OrderId, Guid TenantId, Guid? UserId);

public enum OwnerOutcome
{
    /// <summary>The order has a user: its rebuilt OrderConfirmed is sent and fills the snapshot's owner.</summary>
    Missing = 1,

    /// <summary>A guest order (no user). Nothing to fill — the snapshot stays ownerless and closed to self-service.</summary>
    Guest = 2,

    /// <summary>Ordering has no such order (or it is outside the selected tenants). Never sent.</summary>
    NoOrder = 3,
}

public sealed record OwnerVerdict(Guid OrderId, OwnerOutcome Outcome, Guid? TenantId);

public sealed record SupportOrderOwnerPlan(IReadOnlyList<OwnerVerdict> Verdicts, IReadOnlyList<OwnerVerdict> ToSend)
{
    public int Count(OwnerOutcome outcome) => Verdicts.Count(v => v.Outcome == outcome);

    public int Missing => Count(OwnerOutcome.Missing);
}

/// <summary>
/// Pure: classifies each ownerless snapshot by its Ordering order and picks what to send — oldest first (UUIDv7 order
/// ids sort by creation), capped at <c>--limit</c>.
/// </summary>
public static class SupportOrderOwnerPlanner
{
    public static SupportOrderOwnerPlan Plan(
        IReadOnlyCollection<Guid> ownerlessSnapshots, IReadOnlyCollection<OrderOwnerFact> orders, IReadOnlySet<Guid> tenants, int? limit)
    {
        var byId = orders
            .Where(o => tenants.Count == 0 || tenants.Contains(o.TenantId))
            .ToDictionary(o => o.OrderId);
        var verdicts = ownerlessSnapshots
            .Order()
            .Select(id => byId.TryGetValue(id, out var o)
                ? new OwnerVerdict(id, o.UserId is null ? OwnerOutcome.Guest : OwnerOutcome.Missing, o.TenantId)
                : new OwnerVerdict(id, OwnerOutcome.NoOrder, null))
            .ToList();
        var missing = verdicts.Where(v => v.Outcome == OwnerOutcome.Missing);
        return new SupportOrderOwnerPlan(verdicts, (limit is { } n ? missing.Take(n) : missing).ToList());
    }
}
