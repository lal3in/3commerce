using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Tools.OrderConfirmedBackfill.Storefronts;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Tests;

/// <summary>
/// One tenant's storefronts, publications, payment accounts and carriers, built the way the live code builds them:
/// <see cref="Duplicate"/> copies publications exactly like Catalog's duplicate endpoint (PublishedAt verbatim, created
/// at the clone's instant) and the two consumer helpers clone rows with the services' own <c>CloneForStorefront</c>.
/// </summary>
internal sealed class StorefrontWorld
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly List<StorefrontFact> _storefronts = [];
    private readonly List<DuplicationFact> _duplications = [];
    private readonly List<PublicationFact> _publications = [];

    public Guid Tenant { get; } = Guid.CreateVersion7();
    public List<PaymentAccount> Accounts { get; } = [];
    public List<CarrierIntegration> Carriers { get; } = [];

    public StorefrontFact Store(string name, DateTimeOffset at)
    {
        var store = new StorefrontFact(Guid.CreateVersion7(), Tenant, name, at, Archived: false);
        _storefronts.Add(store);
        return store;
    }

    public void Archive(StorefrontFact store)
    {
        _storefronts.Remove(store);
        _storefronts.Add(store with { Archived = true });
    }

    /// <summary>Set the storefront's recorded source link (as Catalog would have stored it at duplication).</summary>
    public StorefrontFact Relink(StorefrontFact store, Guid? source)
    {
        var linked = store with { DuplicatedFromStorefrontId = source };
        _storefronts[_storefronts.FindIndex(s => s.Id == store.Id)] = linked;
        return linked;
    }

    public void Vanish(StorefrontFact store) => _storefronts.RemoveAll(s => s.Id == store.Id);

    /// <summary>Assign a new product and publish it (first publish sets PublishedAt).</summary>
    public Guid Publish(StorefrontFact store, DateTimeOffset at)
    {
        var product = Guid.CreateVersion7();
        _publications.Add(new PublicationFact(store.Id, product, at, at.AddSeconds(5)));
        return product;
    }

    /// <summary>
    /// Catalog's duplicate endpoint: a new storefront + copies of the source's publications. <paramref name="linked"/>
    /// = the copy records its source (<c>DuplicatedFromStorefrontId</c>, every copy made since the link existed);
    /// false = an older copy without it.
    /// </summary>
    public StorefrontFact Duplicate(StorefrontFact source, string name, DateTimeOffset at, bool audited = true, bool linked = false)
    {
        var clone = Store(name, at);
        if (linked)
        {
            clone = Relink(clone, source.Id);
        }

        foreach (var p in _publications.Where(p => p.StorefrontId == source.Id && p.CreatedAt < at && p.ProductPublishedAt < at).ToList())
        {
            _publications.Add(new PublicationFact(clone.Id, p.ProductId, at, p.ProductPublishedAt));
        }

        if (audited)
        {
            _duplications.Add(new DuplicationFact(Tenant, clone.Id, name));
        }

        return clone;
    }

    public PaymentAccount Account(StorefrontFact store, string name, DateTimeOffset at, string provider = "mock", string? externalRef = null)
    {
        var account = PaymentAccount.Create(Tenant, store.Id, name, provider, PaymentProviderMode.Test, isDefaultForStorefront: true, externalRef, at);
        Accounts.Add(account);
        return account;
    }

    public CarrierIntegration Carrier(StorefrontFact store, CarrierCode code, DateTimeOffset at, bool activate = false)
    {
        var carrier = CarrierIntegration.Configure(Tenant, store.Id, code, code == CarrierCode.Fake ? null : "secret://carrier", at);
        if (activate)
        {
            carrier.Activate(at);
        }

        Carriers.Add(carrier);
        return carrier;
    }

    /// <summary>What Payments' StorefrontDuplicatedConsumer does: no-op if the target has accounts, else clone the source's.</summary>
    public void PaymentsConsumer(Guid source, Guid target, DateTimeOffset at)
    {
        if (Accounts.Any(a => a.StorefrontId == target))
        {
            return;
        }

        Accounts.AddRange(Accounts.Where(a => a.StorefrontId == source).ToList().Select(a => a.CloneForStorefront(target, at)));
    }

    /// <summary>What CarrierService.CloneStorefrontCarriersAsync does, for Fulfillment's consumer.</summary>
    public void FulfillmentConsumer(Guid source, Guid target, DateTimeOffset at)
    {
        if (Carriers.Any(c => c.StorefrontId == target))
        {
            return;
        }

        Carriers.AddRange(Carriers.Where(c => c.StorefrontId == source).ToList().Select(c => c.CloneForStorefront(target, at)));
    }

    /// <summary>Deliver every selected backfill send to its consumer — what happens after --execute.</summary>
    public void Deliver(StorefrontBackfillPlan plan, DateTimeOffset at)
    {
        foreach (var v in plan.Payments)
        {
            var e = StorefrontBackfillQueues.Rebuild(v, StorefrontBackfillTarget.Payments);
            PaymentsConsumer(e.SourceStorefrontId, e.NewStorefrontId, at);
        }

        foreach (var v in plan.Fulfillment)
        {
            var e = StorefrontBackfillQueues.Rebuild(v, StorefrontBackfillTarget.Fulfillment);
            FulfillmentConsumer(e.SourceStorefrontId, e.NewStorefrontId, at);
        }
    }

    public StorefrontTenantSnapshot Snapshot() => new(
        Tenant,
        _storefronts.ToList(),
        _duplications
            .Concat(StorefrontBackfillPlanner.ProvenByLink(Tenant, _storefronts))
            .Concat(StorefrontBackfillPlanner.ProvenByPublications(Tenant, _storefronts, _publications)).ToList(),
        _publications.ToList(),
        Accounts.Select(ConfigRowFact.From).ToList(),
        Carriers.Select(ConfigRowFact.From).ToList());

    public IReadOnlyList<DuplicationVerdict> Plan() =>
        StorefrontBackfillPlanner.Plan(Snapshot(), StorefrontBackfillPlanner.DefaultCloneWindow);

    public DuplicationVerdict Verdict(StorefrontFact target) => Plan().Single(v => v.TargetId == target.Id);
}
