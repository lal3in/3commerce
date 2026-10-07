using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

public class OfferResolutionTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Variant = Guid.NewGuid();

    private static readonly Guid Supplier = Guid.NewGuid();

    private static OfferCopy Offer(Guid? variant, FulfilmentType type, int priority, bool active = true, Guid? supplierId = null) =>
        new()
        {
            OfferId = Guid.NewGuid(),
            TenantId = Tenant,
            ProductId = Product,
            VariantId = variant,
            SupplierId = supplierId ?? Supplier,
            FulfilmentType = type,
            Priority = priority,
            Active = active,
        };

    [Fact]
    public void No_offers_resolves_to_unassigned() =>
        Assert.Equal(FulfilmentType.Unassigned, OfferResolution.ResolveFulfilment([], Tenant, Product, Variant));

    [Fact]
    public void Variant_specific_offer_beats_product_level()
    {
        var offers = new[] { Offer(null, FulfilmentType.Dropship, 0), Offer(Variant, FulfilmentType.Warehouse, 5) };
        Assert.Equal(FulfilmentType.Warehouse, OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant));
    }

    [Fact]
    public void Lowest_priority_wins_among_the_same_grain()
    {
        var offers = new[] { Offer(Variant, FulfilmentType.Dropship, 10), Offer(Variant, FulfilmentType.Warehouse, 1) };
        Assert.Equal(FulfilmentType.Warehouse, OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant));
    }

    [Fact]
    public void Product_level_offer_applies_when_no_variant_specific_one_exists()
    {
        var offers = new[] { Offer(null, FulfilmentType.Dropship, 0) };
        Assert.Equal(FulfilmentType.Dropship, OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant));
    }

    [Fact]
    public void Inactive_and_other_tenant_offers_are_ignored()
    {
        var offers = new[]
        {
            Offer(Variant, FulfilmentType.Warehouse, 1, active: false),
            new OfferCopy
            {
                OfferId = Guid.NewGuid(), TenantId = Guid.NewGuid(), ProductId = Product, VariantId = Variant,
                FulfilmentType = FulfilmentType.Dropship, Priority = 0, Active = true,
            },
        };
        Assert.Equal(FulfilmentType.Unassigned, OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant));
    }

    // --- Offer-as-price selection (ResolvePricingOffer): storefront + window + currency + active ---

    private static readonly Guid Store = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static OfferCopy PricingOffer(
        Guid? variant, long price, int priority = 0, bool active = true, Guid? storefrontId = null,
        string currency = "EUR", DateTimeOffset? from = null, DateTimeOffset? until = null, Guid? supplierId = null) =>
        new()
        {
            OfferId = Guid.NewGuid(),
            TenantId = Tenant,
            ProductId = Product,
            VariantId = variant,
            SupplierId = supplierId ?? Supplier,
            PriceMinor = price,
            Priority = priority,
            Active = active,
            StorefrontId = storefrontId,
            Currency = currency,
            ActiveFrom = from,
            ActiveUntil = until,
        };

    private static long? Price(IEnumerable<OfferCopy> offers, Guid? variant = null, Guid? store = null, string currency = "EUR") =>
        OfferResolution.ResolvePricingOffer(offers, Tenant, Product, variant, store ?? Store, currency, Now)?.PriceMinor;

    [Fact]
    public void No_effective_offer_returns_null_so_checkout_keeps_the_catalog_price() =>
        Assert.Null(Price([]));

    [Fact]
    public void An_all_storefront_offer_prices_any_storefront_of_its_currency() =>
        Assert.Equal(1_500, Price([PricingOffer(null, 1_500, storefrontId: null)]));

    [Fact]
    public void A_storefront_scoped_offer_only_prices_its_own_storefront()
    {
        var offers = new[] { PricingOffer(null, 1_500, storefrontId: Guid.NewGuid()) };
        Assert.Null(Price(offers)); // scoped to a different store
        Assert.Equal(1_500, Price([PricingOffer(null, 1_500, storefrontId: Store)]));
    }

    [Fact]
    public void A_storefront_scoped_offer_beats_an_all_storefront_one_at_the_same_grain()
    {
        var offers = new[]
        {
            PricingOffer(null, 2_000, storefrontId: null),
            PricingOffer(null, 1_200, storefrontId: Store),
        };
        Assert.Equal(1_200, Price(offers));
    }

    [Fact]
    public void A_variant_specific_offer_beats_a_product_level_one()
    {
        var offers = new[] { PricingOffer(null, 2_000), PricingOffer(Variant, 900) };
        Assert.Equal(900, Price(offers, variant: Variant));
    }

    [Fact]
    public void Lowest_priority_wins_among_the_same_grain_and_scope()
    {
        var offers = new[]
        {
            PricingOffer(null, 2_000, priority: 10, storefrontId: Store),
            PricingOffer(null, 1_100, priority: 1, storefrontId: Store),
        };
        Assert.Equal(1_100, Price(offers));
    }

    [Fact]
    public void An_offer_outside_its_active_window_does_not_price()
    {
        Assert.Null(Price([PricingOffer(null, 1_500, from: Now.AddDays(1))])); // not started
        Assert.Null(Price([PricingOffer(null, 1_500, until: Now.AddDays(-1))])); // expired
        Assert.Equal(1_500, Price([PricingOffer(null, 1_500, from: Now.AddDays(-1), until: Now.AddDays(1))]));
    }

    [Fact]
    public void An_offer_in_a_different_currency_does_not_price() =>
        Assert.Null(Price([PricingOffer(null, 1_500, currency: "USD")], currency: "EUR"));

    [Fact]
    public void An_inactive_or_zero_price_offer_does_not_price()
    {
        Assert.Null(Price([PricingOffer(null, 1_500, active: false)]));
        Assert.Null(Price([PricingOffer(null, 0)]));
    }

    // --- Approval gating (DECISION A, strict): an unapproved supplier's offer never counts ---

    [Fact]
    public void An_unapproved_suppliers_offer_is_not_resolved_for_fulfilment()
    {
        var offers = new[] { Offer(Variant, FulfilmentType.Warehouse, 1, supplierId: Supplier) };
        // No approved suppliers → the only covering offer is ignored → Unassigned (no valid supply).
        Assert.Equal(FulfilmentType.Unassigned,
            OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant, new HashSet<Guid>()));
        // Approving the supplier makes the offer count again.
        Assert.Equal(FulfilmentType.Warehouse,
            OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant, new HashSet<Guid> { Supplier }));
    }

    [Fact]
    public void An_approved_offer_is_preferred_over_a_better_grained_unapproved_one()
    {
        var approvedSupplier = Guid.NewGuid();
        var offers = new[]
        {
            // Variant-specific but UNAPPROVED (would normally win on grain)...
            Offer(Variant, FulfilmentType.Dropship, 0, supplierId: Guid.NewGuid()),
            // ...loses to the product-level APPROVED offer, which is the only valid supply.
            Offer(null, FulfilmentType.Warehouse, 5, supplierId: approvedSupplier),
        };
        Assert.Equal(FulfilmentType.Warehouse,
            OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant, new HashSet<Guid> { approvedSupplier }));
    }

    [Fact]
    public void An_unapproved_suppliers_offer_does_not_set_the_price()
    {
        var offers = new[] { PricingOffer(null, 1_500, supplierId: Supplier) };
        Assert.Null(OfferResolution.ResolvePricingOffer(
            offers, Tenant, Product, null, Store, "EUR", Now, new HashSet<Guid>())?.PriceMinor);
        Assert.Equal(1_500, OfferResolution.ResolvePricingOffer(
            offers, Tenant, Product, null, Store, "EUR", Now, new HashSet<Guid> { Supplier })?.PriceMinor);
    }

    [Fact]
    public void A_null_approval_set_leaves_resolution_ungated()
    {
        // The pre-approval callers (order-line COGS where the supplier was already gated at checkout) pass
        // null and keep resolving every active offer.
        var offers = new[] { Offer(Variant, FulfilmentType.Warehouse, 1) };
        Assert.Equal(FulfilmentType.Warehouse, OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant));
    }

    // --- Supply availability (IsSupplyAvailable): Catalog's ADR-0048 gate, per storefront + currency (ADR-0059) ---

    private static readonly Guid OtherSupplier = Guid.NewGuid();

    private static bool Available(IEnumerable<OfferCopy> offers, IReadOnlySet<Guid> approved, string currency = "EUR") =>
        OfferResolution.IsSupplyAvailable(offers, Tenant, Product, Variant, Store, currency, approved);

    [Fact]
    public void An_offerless_line_is_available_the_catalogue_price_governs_it() =>
        Assert.True(Available([], new HashSet<Guid>()));

    [Fact]
    public void A_line_whose_only_covering_offer_is_unapproved_is_unavailable() =>
        Assert.False(Available([PricingOffer(null, 0, supplierId: Supplier)], new HashSet<Guid>()));

    [Fact]
    public void One_approved_covering_offer_makes_the_line_available()
    {
        var offers = new[] { PricingOffer(null, 0, supplierId: Supplier), PricingOffer(Variant, 0, supplierId: OtherSupplier) };
        Assert.True(Available(offers, new HashSet<Guid> { OtherSupplier }));
    }

    [Fact]
    public void An_offer_approved_only_for_another_storefront_does_not_unlock_this_one()
    {
        // The pre-ADR-0059 defect: checkout accepted the line because SOME active approved offer existed.
        var offers = new[]
        {
            PricingOffer(null, 0, supplierId: Supplier, storefrontId: Guid.NewGuid()), // approved, elsewhere
            PricingOffer(null, 0, supplierId: OtherSupplier, storefrontId: Store),     // unapproved, here
        };
        Assert.False(Available(offers, new HashSet<Guid> { Supplier }));
    }

    [Fact]
    public void An_offer_approved_only_in_another_currency_does_not_unlock_this_one()
    {
        var offers = new[]
        {
            PricingOffer(null, 0, supplierId: Supplier, currency: "USD"),      // approved, other currency
            PricingOffer(null, 0, supplierId: OtherSupplier, currency: "EUR"), // unapproved, this currency
        };
        Assert.False(Available(offers, new HashSet<Guid> { Supplier }));
    }

    [Fact]
    public void An_unapproved_offer_for_another_storefront_or_currency_does_not_block_this_one()
    {
        // The other half: Catalog lists such a product (nothing covers it here), so checkout must sell it.
        var offers = new[]
        {
            PricingOffer(null, 0, supplierId: Supplier, storefrontId: Guid.NewGuid()),
            PricingOffer(null, 0, supplierId: Supplier, currency: "USD"),
        };
        Assert.True(Available(offers, new HashSet<Guid>()));
    }

    [Fact]
    public void Coverage_matches_currency_case_insensitively_and_ignores_the_active_window()
    {
        // Catalog does not apply the window to coverage (only to the offer price) — neither does checkout.
        var expired = PricingOffer(null, 0, supplierId: Supplier, currency: "eur", until: Now.AddDays(-1));
        Assert.False(Available([expired], new HashSet<Guid>()));
        Assert.True(Available([expired], new HashSet<Guid> { Supplier }));
    }

    [Fact]
    public void Inactive_other_tenant_and_other_variant_offers_never_cover()
    {
        var offers = new[]
        {
            PricingOffer(null, 0, supplierId: Supplier, active: false),
            PricingOffer(Guid.NewGuid(), 0, supplierId: Supplier),
            new OfferCopy { OfferId = Guid.NewGuid(), TenantId = Guid.NewGuid(), ProductId = Product, SupplierId = Supplier, Active = true, Currency = "EUR" },
        };
        Assert.True(Available(offers, new HashSet<Guid>()));
    }
}
