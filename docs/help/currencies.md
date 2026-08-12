# Currencies

The set of currencies the platform prices, sells, and settles in is a **managed
registry** owned by the **Entity** service (reference/master data, ADR-0027).
Operators curate it from the Admin **Currencies** page; every other screen and
service reads a local projection of it rather than the registry database. Money
everywhere is stored as integer **minor units** and displayed with each
currency's real number of decimals.

## Managing currencies — `/currencies`

File: `src/Admin/Components/Pages/Currencies.razor`; backed by
`/api/entity/admin/currencies`. The `admin` role is required. The page is
tenant-scoped (seeded default tenant `00000000-0000-0000-0000-000000000001`) and
lists every currency for the tenant, including disabled ones.

- **Add a currency** — fill the top form: **Code** (ISO 4217, exactly 3 letters,
  upper-cased for you), **Name**, **Symbol**, and **Decimals** (`0`–`4`). **Add**
  creates it enabled. A duplicate code is rejected (`409`); a bad code or
  out-of-range decimals is rejected with the API reason shown in the banner.
- **Edit** — the row's **Edit** button makes Name, Symbol, and Decimals editable
  inline; **Save** writes them. The code is the identity and is not editable —
  retire and re-add if you must change it.
- **Enable / Disable** — toggles whether the currency can be *newly* chosen.
  Disabled rows stay in the list, dimmed. See
  [Disable is forward-only](#disable-is-forward-only).

> **Seeded set.** So the registry is never empty on first boot, the platform
> seeds **AUD, CAD, CNY, EUR, GBP, USD** (2-decimal) plus **JPY** as the seeded
> **0-decimal** example (¥1,500, not ¥15.00) — see
> `src/Services/Entity/Api/CurrencySeeder.cs` (idempotent). Everything else you
> add here; e.g. **KWD** is a 3-decimal example.

Every mutation publishes a `CurrencyChanged` event and records an audit entry
(`entity.currency.create|update|enable|disable`), so the change is traceable in
Mission Control and picked up by the projections below without any service
reading the Entity database directly.

## Where currencies show up

Once a currency is **registered and enabled**, it flows out through the
`CurrencyChanged` projection (Catalog's `SupportedCurrency` read model mirrors
it):

- **Pickers** — the storefront/commerce config and product/variant price editors
  offer the tenant's **enabled** codes in a dropdown (`CurrencySelect`), never a
  free-typed box.
- **Validation** — storefront commerce config and product/offer pricing validate
  the chosen currency against the projection: only a **registered, enabled** code
  may be newly used. An unregistered or disabled code is **rejected** on save.
- **Financial screens** — the Admin **Dashboard**, **Financials**, **Mission
  Control**, and the **Ledger** are data-driven from ledger balances, so any
  currency that has activity appears there automatically.

> On a brand-new environment, before the projection has caught up, `CurrencySelect`
> falls back to the full static ISO 4217 list so pickers are never empty; once the
> registry projects, the tenant's curated set takes over.

## Decimal places

Not every currency has 2 decimals. Money is shown and entered with each
currency's real minor-unit exponent:

| Currency | Decimals | Displayed |
|----------|----------|-----------|
| `JPY`, `KRW`, `VND` … | 0 | ¥1,500 |
| Most currencies (`USD`, `EUR`, `AUD` …) | 2 | $19.95 |
| `KWD`, `BHD`, `OMR` … | 3 | KWD 1.250 |

Amounts are always stored as a `long` count of the **smallest unit** (`…Minor`);
the divisor between minor and major is `10^decimals`. The ISO 4217 exponent table
(`src/BuildingBlocks/Contracts/Reference/CurrencyDecimals.cs`, mirrored in the
Storefront's `lib/money.ts`) is the single authority at both store-time and
display-time, so the two always agree.

> **Changing a currency's *Decimals* is a display/parse setting only.** It changes
> how amounts are formatted and how typed input is scaled into minor units — it
> does not migrate or reinterpret stored balances.

## Disable is forward-only

Disabling a currency is deliberately **non-destructive**. A disabled currency:

- **cannot be chosen** for a new storefront, commerce config, or product/variant
  price — it drops out of the pickers and fails validation if submitted; but
- **still displays everywhere it already has history** — existing prices, orders,
  and ledger balances in that currency keep rendering correctly, because the
  disabled row stays in the projection so its symbol and decimals still resolve.

When you edit a record that already uses a now-disabled code, the picker keeps
that code selectable so the edit never silently drops it. Re-enable at any time to
make it choosable again for new work.

## API surface

| Purpose | Read | Action |
|---------|------|--------|
| Currency registry | `GET /api/entity/admin/currencies?tenantId=…&includeDisabled=true` | `POST /api/entity/admin/currencies` · `PUT …/{code}` · `POST …/{code}/enable\|disable` |

**Create** takes `{ tenantId, code, name, symbol, decimalPlaces }` with `code` a
3-letter ISO string and `decimalPlaces` in `0`–`4`; **Update** takes
`{ tenantId, name, symbol, decimalPlaces }`. Every write publishes
`CurrencyChanged` and records an audit event.
