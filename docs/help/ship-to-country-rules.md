# Ship-to countries & per-country ship rules

Where a storefront **ships**, and how **tax and shipping** vary **per product per destination** — plus the
country/region fields on the checkout and address forms that feed it. Accurate to the code. Two independent,
tenant-scoped layers, both authored in **Catalog** and applied at checkout in **Ordering** from local read
copies (ADR-0008 — no cross-service reads). See ADR-0050.

## Contents

- [The two layers at a glance](#the-two-layers-at-a-glance)
- [Ship-to allowlist (storefront-wide)](#ship-to-allowlist-storefront-wide)
- [Per-product ship rules](#per-product-ship-rules)
- [Mandatory-rules tenant switch](#mandatory-rules-tenant-switch)
- [How checkout applies them (money math)](#how-checkout-applies-them-money-math)
- [Country & region (State/Province) fields](#country--region-stateprovince-fields)
- [Endpoints & contracts](#endpoints--contracts)
- [Related ADRs and pages](#related-adrs-and-pages)

## The two layers at a glance

| Layer | Scope | Question it answers | Effect at checkout |
|-------|-------|---------------------|--------------------|
| **Ship-to allowlist** | Storefront-wide, all-or-nothing | *Can this store ship here at all?* | Rejects an out-of-allowlist destination before any payment |
| **Per-product ship rules** | Per product, per destination | *For this product to this country, is destination tax charged? Is shipping already covered?* | Excludes exempt lines from the tax base; waives shipping when every line is covered |

The allowlist is a **reachability guardrail**; ship rules are **tax/shipping modifiers**. They are
independent — **a ship rule never widens the allowlist**; a destination must pass the allowlist first.

## Ship-to allowlist (storefront-wide)

Each storefront carries `Storefront.ShipToCountries`, a list of **ISO 3166-1 alpha-2** codes.

- **Empty = ships worldwide** (no restriction). A non-empty list is an **allowlist**.
- Set it on the Admin **Commerce ops** storefront **Manage** form.
- The list rides **`StorefrontConfigChanged`** into Ordering's **`StorefrontTaxCopy.ShipToCountries`**.
- **Checkout rejects**, before authorizing any payment, an order whose ship-to country is outside a
  non-empty allowlist: *"This storefront does not ship to XX."* The storefront checkout country picker is
  limited to served countries.
- Codes are validated (2 letters A–Z), upper-cased and de-duplicated. Passing **null** leaves the current
  list untouched, so an older admin client that doesn't send the field can't silently wipe it
  (`Storefront.SetShipToCountries`).

## Per-product ship rules

Each product carries `Product.ShipRules`, a list of
**`ProductShipRule(CountryCode, ChargeDestinationTax, ShippingCovered)`**. A rule targets one
destination — an **ISO-2 country code**, or the **`*` whole-world default** — and declares two booleans:

| Field | When | Effect |
|-------|------|--------|
| `ChargeDestinationTax` | **false** | That product's line is **excluded from the destination tax base** for that country (the shopper still pays the full line — only the tax portion shrinks). |
| `ShippingCovered` | **true** | Shipping is already covered for that product to that country. When **every** line in the cart resolves to a covered rule, the order's shipping is **waived** to 0. |

**Precedence (`ProductCopy.RuleFor`)**: a **specific-country** rule wins over the **`*`** default; if
neither matches, the line is taxed and shipped normally. Rules are normalized on save (upper-cased ISO or
`*`, de-duplicated by country, last rule wins). On the wire, **null** means "no per-country overrides
carried" — the projection **leaves existing rules as-is** (so a CSV re-import that omits the field can't
wipe Ordering's copy); an **explicit empty list clears** them.

Shipping waiver is **all-or-nothing at order level** — order-flat shipping can't be partially attributed to
some lines, so it drops to 0 only when *every* line is covered.

## Mandatory-rules tenant switch

`TenantCatalogSettings.RequireProductShipRules` (a tenant-scoped feature switch) makes at least one rule
**mandatory** at product create/update.

- **ON** — a product write that would leave the product with **no** rules returns **400**
  (`ValidationProblem`, key `ShipRules`). A write that **omits** the field on a product that already has
  rules keeps them and passes — the gate fires only when the product would end up with none (a new product
  with null/empty, or an explicit empty list that clears them).
- A **malformed** country code (e.g. `"USA"`, `"1X"`) returns a 400 `ValidationProblem`, not a 500.
- Toggle it and edit the per-product rule rows (country `<select>` incl. "All countries" `*`, the two
  checkboxes, add/remove) on the Admin **Catalog** editor (`Components/Pages/Catalog.razor`).

**Operator flow**: Admin → Catalog → toggle *Require per-country ship rules* → a new product with no rule ⇒
save blocked → add a `*` rule with `ChargeDestinationTax=false` ⇒ saves → that product at storefront
checkout shows a **0 tax** line for the destination.

## How checkout applies them (money math)

At checkout Ordering loads the storefront's `StorefrontTaxCopy` (allowlist + tax rate/inclusiveness) and
each cart product's `ProductCopy.ShipRules`, then:

1. **Allowlist guard** (before payment) — reject an unserved destination.
2. **Taxable subtotal** — sum only the lines whose resolved rule is **not** `ChargeDestinationTax=false`.
   The **full subtotal is still charged**; only the tax base shrinks.
3. **Shipping waiver** — if every line's rule marks `ShippingCovered=true`, set `shippingMinor = 0` (after
   the selected-shipping quote guards, so a covered order still validates its quote).
4. **Tax** — apply the storefront rate (ADR-0038 inclusive vs exclusive) to the taxable base. Shipping
   follows the goods' taxability: when no goods are destination-taxable (`taxableSubtotal == 0`), shipping
   is not destination-taxed either, so a fully-exempt cart yields **tax = 0** while the shopper still pays
   goods + shipping.

Money stays **integer minor units** and **trial-balanced** — the payment-intent total is unchanged; only
the tax base and shipping charge inputs are recomputed, so the per-storefront ledger (ADR-0045) still nets
0. Verified by `tests/3commerce.IntegrationTests/MoneyFlowTests.cs` (`TrialBalance == 0`).

## Country & region (State/Province) fields

Country is a full **ISO 3166 dropdown** everywhere an address is captured — Admin entity addresses and
supplier bank country (`CountrySelect` over the 249-country `Countries` vocabulary) and the storefront
checkout + account address forms (shared `src/Storefront/lib/countries.ts`) — not a 2-char text box.

The **region** field's *label* adapts to the selected country — **AU→State, CA→Province, UK→County,
CO→Department, JP→Prefecture**, … — via `Countries.RegionLabel` (Admin) / `regionLabel()` (storefront).
Region is stored **end-to-end**: `Order.ShipRegion` and `CheckoutAttempt.ShipRegion` (and `ShipToInfo` so
Fulfillment gets it for shipping labels), and Identity `Address.Region`.

## Endpoints & contracts

| Surface | Detail |
|---------|--------|
| `GET/PUT /api/catalog/admin/settings` | `CatalogSettingsResponse`/`Request` `{ tenantId, requireProductShipRules }` — the mandatory-rules switch |
| Product writes (`/api/catalog/admin/products`) | accept a trailing `shipRules: [{ countryCode, chargeDestinationTax, shippingCovered }]` |
| Storefront config writes | accept `shipToCountries: string[]` |
| `ProductUpserted.ShipRules` | `ProductShipRuleContract(CountryCode, ChargeDestinationTax, ShippingCovered)?` — optional/back-compatible (null = a publisher predating the field, "no overrides") → Ordering `ProductCopy.ShipRules` |
| `StorefrontConfigChanged.ShipToCountries` | `string[]?` (null = no restriction) → Ordering `StorefrontTaxCopy.ShipToCountries` |

Both jsonb collections use an explicit `ValueComparer<List<record>>` in `CatalogDbContext` and
`OrderingDbContext` so EF tracks them structurally. See `docs/api/api_contracts_index.md`.

## Related ADRs and pages

| Reference | Why it matters here |
|-----------|--------------------|
| [ADR-0050](../adr/0050-per-country-ship-rules-and-ship-to-allowlist.md) | The decision — both layers, projection, and money math |
| [ADR-0016](../adr/0016-global-shipping-dap-tax-at-home.md) | Baseline "ship worldwide DAP, tax at home" that the allowlist and rules refine |
| [ADR-0038](../adr/0038-per-currency-shelf-prices-and-tax-entry.md) | Per-currency shelf prices + inclusive/exclusive tax convention the tax base plugs into |
| [ADR-0042](../adr/0042-physical-shipping-carriers-and-product-type-policy.md) | Only shippable carts pay shipping — the gate the `ShippingCovered` waiver sits alongside |
| [ADR-0008](../adr/0008-database-per-service-single-postgres.md) | Read-copy projections (Catalog → Ordering), no cross-service reads |
| [Admin operations](./admin-operations.md) | The Catalog editor (rules + switch) and Commerce ops (allowlist) screens |
