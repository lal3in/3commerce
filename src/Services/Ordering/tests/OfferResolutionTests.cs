using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

public class OfferResolutionTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly Guid Variant = Guid.NewGuid();

    private static readonly Guid Supplier = Guid.NewGuid();

    // The line's storefront + currency. Declared before Offer() uses it as a default.
    private static readonly Guid Store = Guid.NewGuid();

    private static OfferCopy Offer(
        Guid? variant, FulfilmentType type, int priority, bool active = true, Guid? supplierId = null,
        Guid? storefrontId = null, string currency = "EUR", long cost = 0) =>
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
            StorefrontId = storefrontId,
            Currency = currency,
            SupplierCostMinor = cost,
        };

    private static IReadOnlySet<Guid> Approved(params Guid[] suppliers) =>
        suppliers.Length == 0 ? new HashSet<Guid> { Supplier } : suppliers.ToHashSet();

    private static FulfilmentType Fulfil(
        IEnumerable<OfferCopy> offers, IReadOnlySet<Guid>? approved = null, string currency = "EUR") =>
        OfferResolution.ResolveFulfilment(offers, Tenant, Product, Variant, Store, currency, approved ?? Approved());

    private static OfferCopy? Supply(
        IEnumerable<OfferCopy> offers, IReadOnlySet<Guid>? approved = null, string currency = "EUR", Guid? variant = null) =>
        OfferResolution.ResolveSupplyOffer(offers, Tenant, Product, variant ?? Variant, Store, currency, approved ?? Approved());

    [Fact]
    public void No_offers_resolves_to_unassigned() =>
        Assert.Equal(FulfilmentType.Unassigned, Fulfil([]));

    [Fact]
    public void Variant_specific_offer_beats_product_level()
    {
        var offers = new[] { Offer(null, FulfilmentType.Dropship, 0), Offer(Variant, FulfilmentType.Warehouse, 5) };
        Assert.Equal(FulfilmentType.Warehouse, Fulfil(offers));
    }

    [Fact]
    public void Lowest_priority_wins_among_the_same_grain()
    {
        var offers = new[] { Offer(Variant, FulfilmentType.Dropship, 10), Offer(Variant, FulfilmentType.Warehouse, 1) };
        Assert.Equal(FulfilmentType.Warehouse, Fulfil(offers));
    }

    [Fact]
    public void Product_level_offer_applies_when_no_variant_specific_one_exists()
    {
        var offers = new[] { Offer(null, FulfilmentType.Dropship, 0) };
        Assert.Equal(FulfilmentType.Dropship, Fulfil(offers));
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
                SupplierId = Supplier, FulfilmentType = FulfilmentType.Dropship, Priority = 0, Active = true, Currency = "EUR",
            },
        };
        Assert.Equal(FulfilmentType.Unassigned, Fulfil(offers));
    }

    // --- Supply offer = the sell gate's set (ADR-0059): this storefront (or all) × the order's currency ---

    [Fact]
    public void An_offer_only_for_another_storefront_never_supplies_this_one()
    {
        var elsewhere = Guid.NewGuid();
        var here = Guid.NewGuid();
        var offers = new[]
        {
            // Better priority AND variant-specific, but scoped to another store: the old resolver picked it.
            Offer(Variant, FulfilmentType.Dropship, 0, supplierId: elsewhere, storefrontId: Guid.NewGuid(), cost: 4_500),
            Offer(null, FulfilmentType.Warehouse, 9, supplierId: here, storefrontId: Store, cost: 3_000),
        };

        var supply = Supply(offers, Approved(elsewhere, here));

        Assert.NotNull(supply);
        Assert.Equal(here, supply.SupplierId);
        Assert.Equal(FulfilmentType.Warehouse, supply.FulfilmentType);
        Assert.Equal(3_000, supply.SupplierCostMinor);
    }

    [Fact]
    public void An_offer_only_in_another_currency_never_supplies_the_line()
    {
        var usd = Guid.NewGuid();
        var eur = Guid.NewGuid();
        var offers = new[]
        {
            Offer(Variant, FulfilmentType.Dropship, 0, supplierId: usd, currency: "USD", cost: 4_000),
            Offer(null, FulfilmentType.Warehouse, 9, supplierId: eur, currency: "EUR", cost: 2_500),
        };

        Assert.Equal(eur, Supply(offers, Approved(usd, eur))?.SupplierId);
        // With only the foreign-currency offer the line is offerless here: no supplier, Unassigned.
        Assert.Null(Supply([offers[0]], Approved(usd, eur)));
        Assert.Equal(FulfilmentType.Unassigned, Fulfil([offers[0]], Approved(usd, eur)));
    }

    [Fact]
    public void An_all_storefront_offer_in_the_orders_currency_supplies_the_line()
    {
        var offer = Offer(null, FulfilmentType.Dropship, 0, storefrontId: null, currency: "eur", cost: 1_234);
        var supply = Supply([offer]);
        Assert.Same(offer, supply); // currency matched case-insensitively, exactly as the gate does
    }

    [Fact]
    public void The_storefronts_own_offer_beats_an_all_store_offer_at_the_same_grain_whatever_its_priority()
    {
        // Owner, 2026-10-15: price, supply and cost rank alike — variant → store-scoped → Priority.
        var allStores = Offer(null, FulfilmentType.Dropship, 1, storefrontId: null);
        var thisStore = Offer(null, FulfilmentType.Warehouse, 2, storefrontId: Store);
        Assert.Same(thisStore, Supply([allStores, thisStore]));
    }

    [Fact]
    public void An_exact_tie_breaks_on_the_lowest_offer_id_whatever_the_input_order()
    {
        var a = Offer(null, FulfilmentType.Dropship, 3);
        var b = Offer(null, FulfilmentType.Warehouse, 3);
        var expected = a.OfferId.CompareTo(b.OfferId) < 0 ? a : b;
        Assert.Same(expected, Supply([a, b]));
        Assert.Same(expected, Supply([b, a]));
    }

    [Fact]
    public void An_out_of_window_or_price_less_offer_still_supplies_the_line()
    {
        // Coverage ignores the active window and the offer price (the gate does too) — unlike ResolvePricingOffer.
        var offer = Offer(null, FulfilmentType.DigitalDownload, 0);
        offer.ActiveUntil = DateTimeOffset.UtcNow.AddDays(-30);
        Assert.Same(offer, Supply([offer]));
    }

    [Fact]
    public void Whenever_the_gate_passes_a_covered_line_the_supply_offer_is_one_it_counted()
    {
        // Consistency with IsSupplyAvailable over every mix of store scope × currency × approval: when the
        // gate sells a line that has covering offers, the supply offer exists, covers it and is approved;
        // when nothing covers it, there is no supply offer (offerless line).
        var stores = new Guid?[] { null, Store, Guid.NewGuid() };
        var currencies = new[] { "EUR", "USD" };
        var suppliers = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var candidates = (from st in stores
                          from cur in currencies
                          from sup in suppliers
                          select Offer(null, FulfilmentType.Dropship, 0, supplierId: sup, storefrontId: st, currency: cur)).ToList();
        var approved = new HashSet<Guid> { suppliers[0] };

        // Every subset of up to 3 candidate offers.
        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i; j < candidates.Count; j++)
            {
                for (var k = j; k < candidates.Count; k++)
                {
                    var offers = new[] { candidates[i], candidates[j], candidates[k] }.Distinct().ToList();
                    var sellable = OfferResolution.IsSupplyAvailable(offers, Tenant, Product, Variant, Store, "EUR", approved);
                    var covered = offers.Any(o => OfferResolution.Covers(o, Tenant, Product, Variant, Store, "EUR"));
                    var supply = Supply(offers, approved);

                    if (supply is not null)
                    {
                        Assert.True(sellable);
                        Assert.True(OfferResolution.Covers(supply, Tenant, Product, Variant, Store, "EUR"));
                        Assert.Contains(supply.SupplierId, approved);
                    }
                    else if (sellable)
                    {
                        Assert.False(covered); // sold with no supply offer ⇒ genuinely offerless here
                    }
                }
            }
        }
    }

    // --- One offer decides price, supply and cost (ResolveLineOffer; owner, 2026-10-15) ---

    private static OfferCopy LineOffer(
        Guid supplierId, long price, long cost, int priority, Guid? storefrontId = null, Guid? variant = null,
        DateTimeOffset? until = null, string currency = "EUR") =>
        new()
        {
            OfferId = Guid.NewGuid(),
            TenantId = Tenant,
            ProductId = Product,
            VariantId = variant,
            SupplierId = supplierId,
            FulfilmentType = FulfilmentType.Dropship,
            PriceMinor = price,
            SupplierCostMinor = cost,
            Priority = priority,
            Active = true,
            StorefrontId = storefrontId,
            Currency = currency,
            ActiveUntil = until,
        };

    private static OfferCopy? Line(IEnumerable<OfferCopy> offers, Guid store, IReadOnlySet<Guid> approved, Guid? variant = null) =>
        OfferResolution.ResolveLineOffer(offers, Tenant, Product, variant, store, "EUR", Now, approved);

    [Fact]
    public void The_owners_example_store_A_charges_ships_and_costs_its_own_offer_store_B_the_all_store_one()
    {
        // All-store offer: Priority 1, supplier X, cost 6, price 10. Store-A-only offer: Priority 2, supplier Y,
        // cost 8, price 12. Store A → 12 / Y / 8 (its own offer outranks the better Priority); store B → 10 / X / 6.
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var storeA = Guid.NewGuid();
        var storeB = Guid.NewGuid();
        var allStore = LineOffer(x, price: 10, cost: 6, priority: 1);
        var storeAOnly = LineOffer(y, price: 12, cost: 8, priority: 2, storefrontId: storeA);
        var offers = new[] { allStore, storeAOnly };
        var approved = new HashSet<Guid> { x, y };

        var onA = Line(offers, storeA, approved);
        Assert.Same(storeAOnly, onA);
        Assert.Equal((12L, y, 8L), (onA!.PriceMinor, onA.SupplierId, onA.SupplierCostMinor));
        // The charged price is what it was before this change (pricing resolver untouched).
        Assert.Same(onA, OfferResolution.ResolvePricingOffer(offers, Tenant, Product, null, storeA, "EUR", Now, approved));

        var onB = Line(offers, storeB, approved);
        Assert.Same(allStore, onB);
        Assert.Equal((10L, x, 6L), (onB!.PriceMinor, onB.SupplierId, onB.SupplierCostMinor));
    }

    [Fact]
    public void A_price_setting_offer_also_supplies_even_when_a_price_less_offer_outranks_it()
    {
        // The price must not move: a store-scoped offer with no price of its own (catalogue price would
        // stand) ranks above an all-store priced one, yet the all-store offer is what's charged — so it is
        // also what supplies and costs the line. One offer, never a priced X shipped by Y.
        var priced = LineOffer(Guid.NewGuid(), price: 900, cost: 300, priority: 5);
        var priceLess = LineOffer(Guid.NewGuid(), price: 0, cost: 100, priority: 0, storefrontId: Store);
        var approved = new HashSet<Guid> { priced.SupplierId, priceLess.SupplierId };

        Assert.Same(priced, Line([priceLess, priced], Store, approved));
        // With no priced offer at all, the best covering offer supplies (catalogue price stands).
        Assert.Same(priceLess, Line([priceLess], Store, approved));
    }

    [Fact]
    public void An_out_of_window_offer_neither_prices_nor_outranks_the_priced_one_but_supplies_when_alone()
    {
        var expired = LineOffer(Guid.NewGuid(), price: 500, cost: 50, priority: 0, storefrontId: Store, until: Now.AddDays(-1));
        var current = LineOffer(Guid.NewGuid(), price: 700, cost: 70, priority: 9);
        var approved = new HashSet<Guid> { expired.SupplierId, current.SupplierId };

        Assert.Same(current, Line([expired, current], Store, approved));
        Assert.Same(expired, Line([expired], Store, approved)); // coverage ignores the window (gate parity)
    }

    [Fact]
    public void Customer_facing_prices_are_unchanged_and_the_line_offer_is_always_the_priced_one_and_gate_eligible()
    {
        // Sweep store scope × currency × price/no price × window × approval × grain: whenever the pricing
        // resolver (the one that set prices before this change) yields an offer, the line offer IS that offer,
        // so the charged price cannot have moved; whenever it does not, the line offer is a gate-eligible one.
        var approvedSupplier = Guid.NewGuid();
        var unapproved = Guid.NewGuid();
        var candidates = new List<OfferCopy>();
        foreach (var st in new Guid?[] { null, Store, Guid.NewGuid() })
        {
            foreach (var cur in new[] { "EUR", "USD" })
            {
                foreach (var price in new long[] { 0, 1_000 })
                {
                    foreach (var until in new DateTimeOffset?[] { null, Now.AddDays(-1) })
                    {
                        foreach (var sup in new[] { approvedSupplier, unapproved })
                        {
                            foreach (var variant in new Guid?[] { null, Variant })
                            {
                                candidates.Add(LineOffer(sup, price, cost: price / 2, priority: candidates.Count % 3,
                                    storefrontId: st, variant: variant, until: until, currency: cur));
                            }
                        }
                    }
                }
            }
        }

        var approved = new HashSet<Guid> { approvedSupplier };
        var rng = new Random(20261015);
        for (var run = 0; run < 2_000; run++)
        {
            var offers = candidates.OrderBy(_ => rng.Next()).Take(rng.Next(1, 6)).ToList();
            var priced = OfferResolution.ResolvePricingOffer(offers, Tenant, Product, Variant, Store, "EUR", Now, approved);
            var line = OfferResolution.ResolveLineOffer(offers, Tenant, Product, Variant, Store, "EUR", Now, approved);

            if (priced is not null)
            {
                Assert.Same(priced, line);
            }

            if (line is not null)
            {
                Assert.True(OfferResolution.Covers(line, Tenant, Product, Variant, Store, "EUR"));
                Assert.Contains(line.SupplierId, approved);
                Assert.True(OfferResolution.IsSupplyAvailable(offers, Tenant, Product, Variant, Store, "EUR", approved));
            }
        }
    }

    // --- Offer-as-price selection (ResolvePricingOffer): storefront + window + currency + active ---

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
        Assert.Equal(FulfilmentType.Unassigned, Fulfil(offers, new HashSet<Guid>()));
        // Approving the supplier makes the offer count again.
        Assert.Equal(FulfilmentType.Warehouse, Fulfil(offers, new HashSet<Guid> { Supplier }));
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
        Assert.Equal(FulfilmentType.Warehouse, Fulfil(offers, new HashSet<Guid> { approvedSupplier }));
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
