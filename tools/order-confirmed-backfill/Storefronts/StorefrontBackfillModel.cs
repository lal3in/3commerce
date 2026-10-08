using System.Globalization;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Storefronts;

/// <summary>
/// Which consumer of <c>StorefrontDuplicated</c> a backfill is sent to (ADR-0060): Payments copies the source's payment
/// accounts, Fulfillment its carrier integrations. Never both by Publish.
/// </summary>
public enum StorefrontBackfillTarget
{
    Payments = 1,
    Fulfillment = 2,
}

/// <summary>
/// A storefront as Catalog has it now. <see cref="DuplicatedFromStorefrontId"/> is Catalog's durable link from a copy to
/// its source, recorded by the duplicate endpoint — null for storefronts that are not copies AND for copies made before
/// the link existed (whose source the planner infers instead).
/// </summary>
public sealed record StorefrontFact(Guid Id, Guid TenantId, string Name, DateTimeOffset CreatedAt, bool Archived, Guid? DuplicatedFromStorefrontId = null);

/// <summary>
/// A storefront that was made by duplication. <see cref="NameAtDuplication"/> is the audit summary of the
/// <c>catalog.storefront.duplicate</c> entry — the <c>clone.Name</c> the live event carried — or null when the
/// duplication is known only from its copied publications (no audit entry).
/// </summary>
public sealed record DuplicationFact(Guid TenantId, Guid TargetId, string? NameAtDuplication);

/// <summary>
/// A product publication that has been published at least once. <see cref="ProductPublishedAt"/> is set ONCE, on the
/// first publish (<c>PublishedAt ??= now</c>), and a storefront duplication copies it verbatim onto the clone's
/// publication — so a (product, published-at) pair is shared by two storefronts only through duplication.
/// </summary>
public sealed record PublicationFact(Guid StorefrontId, Guid ProductId, DateTimeOffset CreatedAt, DateTimeOffset ProductPublishedAt);

/// <summary>
/// One per-storefront config row a <c>StorefrontDuplicated</c> consumer copies: a payment account or a carrier
/// integration. <see cref="MatchKey"/> is the part a later edit is unlikely to change (provider / carrier code);
/// <see cref="CopyKey"/> is every field the consumer's <c>CloneForStorefront</c> carries, so two rows with equal copy
/// keys are what one copy of the other would produce.
/// </summary>
public sealed record ConfigRowFact(Guid StorefrontId, string MatchKey, string CopyKey, DateTimeOffset CreatedAt)
{
    private const char Sep = '\u001f';

    public static ConfigRowFact From(PaymentAccount a) => new(
        a.StorefrontId,
        a.Provider,
        string.Join(Sep, a.Name, a.Provider, (int)a.Mode, (int)a.State, a.IsDefaultForStorefront, a.ExternalAccountRef ?? string.Empty,
            a.ActivatedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
        a.CreatedAt);

    public static ConfigRowFact From(CarrierIntegration c) => new(
        c.StorefrontId,
        c.Carrier.ToString(),
        string.Join(Sep, c.Carrier, c.CredentialRef ?? string.Empty, (int)c.Status, c.IsDefault),
        c.CreatedAt);
}

/// <summary>Everything the planner needs about one tenant. Rows of all four services, read-only.</summary>
public sealed record StorefrontTenantSnapshot(
    Guid TenantId,
    IReadOnlyList<StorefrontFact> Storefronts,
    IReadOnlyList<DuplicationFact> Duplications,
    IReadOnlyList<PublicationFact> Publications,
    IReadOnlyList<ConfigRowFact> PaymentAccounts,
    IReadOnlyList<ConfigRowFact> CarrierIntegrations);

public enum SideOutcome
{
    /// <summary>The duplicate already has rows on this side (copied, or configured by hand). Never sent — the consumer
    /// would no-op anyway, and nothing on the target is ever overwritten.</summary>
    Present = 1,

    /// <summary>The source had rows on this side when the duplicate was made and the duplicate has none: the copy was
    /// lost to the competing consumer. Sent.</summary>
    Missing = 2,

    /// <summary>The source had no rows on this side when the duplicate was made — there was nothing to copy.</summary>
    SourceEmpty = 3,

    /// <summary>The evidence cannot say what the copy should be. Reported, never sent.</summary>
    Undetermined = 4,

    /// <summary>Not a candidate at all (the target storefront is archived or gone).</summary>
    Skipped = 5,
}

public enum UndeterminedReason
{
    None = 0,

    /// <summary>No published publication was copied and the duplicate's name does not name its source.</summary>
    NoLineage = 1,

    /// <summary>No older storefront matches the duplicate's lineage and copied half — or the storefront its recorded
    /// link names is not one of the tenant's.</summary>
    NoCandidate = 2,

    /// <summary>Several storefronts could be the source and they would copy different things.</summary>
    CandidatesDisagree = 3,
}

/// <summary>The verdict for one side of one duplicated storefront.</summary>
public sealed record SideVerdict(SideOutcome Outcome, Guid? SourceStorefrontId = null, UndeterminedReason Reason = UndeterminedReason.None)
{
    public static readonly SideVerdict Present = new(SideOutcome.Present);
    public static readonly SideVerdict SourceEmpty = new(SideOutcome.SourceEmpty);
    public static readonly SideVerdict Skipped = new(SideOutcome.Skipped);

    public static SideVerdict Undetermined(UndeterminedReason reason) => new(SideOutcome.Undetermined, null, reason);

    public override string ToString() => Outcome switch
    {
        SideOutcome.Missing => $"MISSING (source {SourceStorefrontId})",
        SideOutcome.Undetermined => $"Undetermined ({Reason})",
        _ => Outcome.ToString(),
    };
}

/// <summary>What the planner concluded about one duplicated storefront.</summary>
public sealed record DuplicationVerdict(
    Guid TenantId,
    Guid TargetId,
    string Name,
    DateTimeOffset? CreatedAt,
    string Evidence,
    int Candidates,
    SideVerdict Payments,
    SideVerdict Fulfillment)
{
    public SideVerdict Side(StorefrontBackfillTarget target) =>
        target == StorefrontBackfillTarget.Payments ? Payments : Fulfillment;
}
