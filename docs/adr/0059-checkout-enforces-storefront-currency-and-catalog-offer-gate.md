# 0059 — Checkout enforces the storefront's currency, and its offer/approval gate equals Catalog's

Status: Accepted — implemented
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

* **Which offer supplies a line** (fulfilment type, supplier, billing mode) is still `ResolveOffer` over the
  approved set — not re-scoped to the store/currency. The COGS accrual (`OrderStatusConsumer`) and its RMA
  reversal (`RmaDispositionSetConsumer`) re-derive the cost offer the same way, so changing the line's
  resolver alone would let the stamped supplier and the accrued cost come from different offers. With
  several approved offers across stores, the supplier can therefore still come from an offer that does not
  cover this store (pre-existing; a follow-up would change all three resolutions together).
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
