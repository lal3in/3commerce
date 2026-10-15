# 0059 — Checkout enforces the storefront's currency, and its offer/approval gate equals Catalog's

Status: Accepted — implemented; amended 2026-10-11 (problem+json refusals) and 2026-10-11/15 (Amendment 2: one offer decides price, supply and cost)
Area: Ordering / Catalog / Storefront / pricing
Extends: [0038](./0038-per-currency-shelf-prices-and-tax-entry.md) (storefront ↔ currency is 1:1), [0045](./0045-mandatory-per-storefront-ledger-attribution.md) (a sale books to its store's own accounts), [0047](./0047-storefront-scoped-active-window-offer-price.md) (storefront-scoped, in-window offer price), [0048](./0048-supplier-approval-gated-offer-availability.md) (approval-gated availability)

## Context

Two checkout defects, both the same shape — the rule checkout enforced was not the rule the storefront
showed, so what was shown, what was sellable and what was charged could disagree.

**A — currency.** During the `--data full` demo seed a EUR cart checked out on the AUD storefront and
returned 201: a EUR order booked to an AUD store. The cart cookie is shared by every storefront on a host,
so a shopper who filled a cart on `/eu` and then opened `/au` reached that store's checkout with EUR lines.
Checkout resolved the storefront (and already refused the synthetic default `…0101`) but never compared
the cart's currency with the store's. ADR-0038 makes a storefront's currency 1:1 and ADR-0045 books the sale
to that store's own ledger accounts, so a EUR order on an AUD store posts EUR revenue to an AUD store's
accounts — the per-store P&L and the per-currency P&L stop reconciling.

**B — supplier approval.** Catalog's public listing and detail (ADR-0047/0048) decide availability from the
ACTIVE offers that **cover** the product on *this* storefront (store-scoped or all-store) in *this* currency:
no covering offer → the catalogue price governs (offerless scope guard); otherwise the product/variant is
available only when at least one covering offer's supplier is approved. Ordering's checkout asked a wider
question — *is there any active offer, in any currency or store, from an approved supplier?* So:

* a product whose only supply here is unapproved, but which has an approved offer for another store or
  currency, was **hidden** by the listing yet **buyable** through the API;
* a product with no covering offer here (listed at its catalogue price), but an unapproved offer elsewhere,
  was **listed** yet **refused** at checkout.

## Decision

**1. Checkout enforces the storefront's currency.** After the default-storefront guard, checkout loads the
storefront's projected `StorefrontTaxCopy` (Catalog already projects `Currency` on `StorefrontConfigChanged`
— no contract change) and refuses a cart whose currency differs with **400 problem+json**:
"Cart is in EUR; this store sells in AUD — empty the cart to shop here." It runs before anything is priced,
reserved or authorized, so nothing is booked. A storefront with **no** projected copy has no known currency
and keeps the historical fallback (the same posture ADR-0055 keeps for tax).

**2. Checkout's offer/approval gate equals Catalog's — shown == sellable == charged.**
`OfferResolution.IsSupplyAvailable` is Catalog's predicate, duplicated in Ordering on purpose (no shared
domain logic): covering = `Active` ∧ tenant ∧ product ∧ (variant-specific ∨ product-level) ∧ currency (case-
insensitive) ∧ (all-storefront ∨ this storefront); available = no covering offer ∨ any covering offer's
supplier approved. The active **window is not part of coverage** — Catalog does not apply it there either;
it gates only the offer *price* (`ResolvePricingOffer`, unchanged). A refused line is a **400 problem+json**.

**3. The cart preview reports both, in checkout's words.** `GET /cart/summary` appends `checkoutBlock`
(number: 0 None, 1 CurrencyMismatch, 2 SupplyUnavailable), `checkoutBlockedReason` (checkout's exact
`detail`) and `storefrontCurrency`. A currency-mismatched cart is **not priced as if valid**: the preview
returns the unpriced add-time subtotal with no promotion, storefront discount or renewal. Checkout's problem
carries the same number as a `checkoutBlock` extension. The storefront cart page renders a localized notice.

### What deliberately did not change

* ~~**Which offer supplies a line** (fulfilment type, supplier, billing mode) is still `ResolveOffer` over the
  approved set — not re-scoped to the store/currency.~~ **Done — see Amendment 2 below** (the follow-up this
  bullet recorded: the line's supply, the COGS accrual and its RMA reversal now change together).
* **Renewals.** Payments' `SubscriptionService` charges the subscription's *stored* price and currency off
  session and never passes through checkout, so the currency gate cannot affect them — pinned by a renewal
  test bought under the gate.
* **Existing orders and refunds** are untouched: both gates run only when a new order is created, and the
  only order-creating path is `POST /checkout`.

## Consequences

* A cart filled on one store is refused (and flagged in the cart) on a store of another currency; the
  shopper empties it. Same-currency stores sharing one cart keep working.
* Seeds and E2E helpers must check out in the store's currency (the `--data full` seed already does since
  #281); the seed's extra offer filter is now a strict subset of what checkout accepts.
* Legacy `OfferCopy` rows projected before `OfferChanged` carried a currency (`Currency = ""`) never cover,
  so they behave as offerless for the gate until the offer is next saved and re-projected.

## Verification

`CheckoutGateTests` + `OfferResolutionTests` (pure), `CartCurrencyTests` (mismatch → 400 + preview block,
remedy → 201), `SubscriptionRenewalPriceTests` (renewal under the gate), `CheckoutOfferGateParityTests`
(Catalog listing/detail and Ordering checkout agree, on offers created through Catalog's admin API), and the
storefront `e2e/currency-tax.spec.ts` EU-cart-on-AU-store case.

## Amendment (2026-10-11) — every checkout refusal is problem+json

When this ADR landed only its two gates answered as `application/problem+json`; the older checkout refusals
were still plain JSON strings, contrary to the platform rule that **every** error is problem+json (AGENTS.md
"Error Handling", api.md §4). They now all are, shaped in one place (`OrderingProblems`): a stable `title` per
code, the unchanged sentence as `detail`, and the numeric `checkoutBlock` — `CheckoutBlock` extended
**additively** (1–2 unchanged and still the only values `GET /cart/summary` reports): 3 CartEmpty,
4 StorefrontRequired, 5 MixedCurrencyCart, 6 ShipToNotServed (+ `shipToCountry`), 7 CollectNotEligible,
8 InvalidShippingSelection, 9 ShippingQuoteExpired, 10 CouponRefused (+ the numeric `couponStatus` and
`couponCode`), 11 StorefrontNotOpen, 12–14 the subscription member / verified-email / saved-instrument gates,
15 PricesChanged and 16 PaymentUnavailable. Statuses are unchanged except one: a Payments request timeout is
now **503** (it was a 400, yet the shopper's request was fine and nothing was booked). The 409 price-drift
body is problem+json whose extensions keep the re-priced totals under `CheckoutResponse`'s field names (and
the legacy `message`). Cart mutations follow the same shape with a numeric `cartRefusal` (`CartRefusal`:
1 UnknownProduct, 2 UnknownVariant, 3 NotPricedInCurrency → 404; 4 CurrencyConflict → 409; 5 LineNotFound →
404). The storefront shows the problem's `detail` on a refused checkout instead of a generic failure.
Pinned by `OrderingProblemsTests` (wire values, statuses, titles) and `CheckoutProblemDetailsTests` + the
gate suites through `ProblemAssert` (content type, title, detail, traceId, code).
## Amendment 2 (2026-10-11, extended 2026-10-15) — one offer decides price, supply and cost

**Owner's decisions.** (2026-10-11) The offer that SUPPLIES a line — supplier, fulfilment type, billing mode
and the supplier cost (COGS) booked for it — comes from the same set the gate above checks: approved, active
offers covering THIS storefront (or all storefronts) in the ORDER'S currency. (2026-10-15, "Option A") **One
offer decides price, supply and cost**, all three ranked alike: variant-specific → the storefront's own
(store-scoped) offer over an all-storefront one → lowest `Priority` → lowest `OfferId` (deterministic tie-break).
Customer-facing prices do not change: the price precedence already preferred the store's own offer.

Example: an all-store offer (Priority 1, supplier X, cost 6, price 10) and a store-A-only offer (Priority 2,
supplier Y, cost 8, price 12). Store A charges 12, ships via Y and books 8; store B charges 10, ships via X and
books 6. Before this, store A charged Y's 12 but shipped via and costed X.

**The three resolutions, changed together.**

1. **Checkout (+ cart preview)** — `OfferResolution.ResolveOffer` is replaced by
   `ResolveLineOffer(…, storefrontId, currency, now, approvedSupplierIds)` = the offer whose price is charged
   (`ResolvePricingOffer`, untouched — effective, in-window, priced, approved) or, when no offer sets the price
   and the catalogue price stands, `ResolveSupplyOffer` = `Covers(…) ∧ approved` ranked as above. Every pricing
   candidate is a covering one, so the line offer always lies in the gate's set; a line that passed
   `IsSupplyAvailable` has one — or no covering offer at all (offerless → catalogue price, `Unassigned`, no
   supplier, one-time). A price-less or out-of-window offer never outranks the offer that sets the price: the
   price cannot move, so the supply follows it. The attempt line snapshots the line offer's `SupplierCostMinor`
   as `SupplierUnitCostMinor` beside its `SupplierId`; the order line inherits both. `OrderConfirmed` carries
   the line's supply to Fulfillment unchanged (Fulfillment never resolves offers itself).
2. **Cost booking** — `OrderStatusConsumer` no longer re-resolves offers at confirmation: it accrues each
   supplier line's snapshotted `SupplierUnitCostMinor × Quantity` (DECISION A kept: a supplier suspended
   between checkout and confirmation accrues nothing) and records what it accrued on the line as
   `BookedSupplierCostMinor`, in the same unit of work as the `OrderCostsRecognized` publish.
3. **Reversal** — `RmaDispositionSetConsumer` values returned goods from `Σ BookedSupplierCostMinor` (scaled by
   the refunded share, as before), never from today's offers — an offer edited, re-prioritised, re-scoped or
   deactivated after the sale cannot change what that order's return reverses. Order cancellation needs no
   reversal: only Pending/AwaitingPayment orders cancel, and COGS accrues only at confirmation.

**No-FX relabel removed for COGS.** A covering offer is in the order's currency, so supplier cost can no longer
cross currencies; the ADR-0041 relabel of an offer cost into the order currency is gone (the carrier-cost
relabel is unaffected). An offer only in another currency now leaves the line offerless — no supplier, no COGS.

**Old orders.** Nothing is restated or migrated: migration `SupplyOfferCostSnapshot` adds the three columns
with default 0, so an order confirmed before it reverses nothing via RMA. The owner wipes and re-seeds dev data
after this ships, so every live order goes through the new path.

**Money seams.** Refunds/RMA: reverse exactly what was booked (above). Renewals: Payments renews the stored
subscription price/currency off session — unaffected; a subscription's billing mode now comes from the store's
own line offer, so another store's one-time offer can no longer turn it into a one-time sale. Multi-storefront:
two stores of one currency with different approved suppliers each charge, ship and book their own offer.

**Tie-break note.** `ResolvePricingOffer` (and Catalog's display rule) keep their order unchanged; the `OfferId`
tie-break applies where an exact tie (same grain, scope and Priority) is resolved by `ResolveSupplyOffer`.

**Verification.** `OfferResolutionTests` — the owner's example, store-scoped beats all-store, a price-less or
out-of-window offer never outranks the priced one, the line offer IS the pricing offer whenever one exists
(randomised sweep: prices unchanged) and always lies in the gate's set, store/currency exclusion, variant/
Priority/OfferId; 8 fail on the old resolver. `CheckoutTests` (snapshot rides onto the order). Integration
`SupplyOfferSellGateTests` (the owner's example end to end; two-store supplier/cost/fulfilment + RMA reversal
after an offer edit; order-currency offer beats a better-ranked foreign one; subscription renewal) and
`MoneyFlowTests.An_offer_in_another_currency_neither_supplies_nor_costs_an_order_line`.
