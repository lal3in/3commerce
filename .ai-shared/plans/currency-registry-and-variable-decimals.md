# Feature: Managed currency registry + variable-decimal money

Admin-managed set of supported currencies (add/remove/enable), enforced on input everywhere, with each
currency's **decimal places** honored across all money display + parsing — so JPY (0), USD (2), KWD (3)
all render/round correctly. The display dashboards (Financials/Dashboard/Mission Control/Ledger) already
derive their currency sections dynamically from the ledger balances, so "show up in the right places" is
mostly free; the work is the registry, enforcement, and variable decimals.

## Decisions (from Q&A)
- **Full variable-decimal support** — the registry carries a decimals field; money math + display honor it.
- **Validate everywhere** — storefront config + product pricing reject any currency not registered+enabled;
  currency pickers only offer registered ones.
- **Neutral reference home** — the registry is tenant reference/master data. Home: the **Entity service**
  (ADR-0027 master-data boundary) — the closest existing neutral owner; other services validate against a
  projected read model, never cross-service DB reads (ADR-0008/0011).

## Current state (grounding)
- No currency allowlist exists — `Storefront.Currency` is a free trimmed-uppercased string (typo "EURO" is
  accepted). Variants carry per-currency prices (`VariantPerCurrencyPrices`, ADR-0038).
- Money is `long …Minor` (smallest unit) everywhere; DISPLAY hardcodes 2 decimals (`/100m`) in 12 C#/razor
  files + the Next.js storefront. A few provider adapters PARSE major→minor assuming ×100 (e.g. PayPal).
- Ledger accounts are NOT per-currency rows — a line carries a Currency; balances are `(account,currency)`.
  So a new currency needs zero ledger provisioning.

## IMPLEMENTATION PLAN — phased (each its own PR, off fresh main)

### Phase 1 — Currency registry + admin management page (currency_1)
1. `Currency` aggregate in Entity.Domain: TenantId, Code (ISO-4217, unique per tenant), Name, Symbol,
   DecimalPlaces (0–4), Enabled, timestamps. Guards: valid code, decimals in range, can't disable a code
   still referenced (soft — disable blocks NEW use, keeps history; see Phase 2 for the reference check).
2. Entity.Api CRUD: `GET/POST/PUT /admin/currencies`, `POST /admin/currencies/{code}/enable|disable`.
   Publish `CurrencyChanged(TenantId, Code, Name, Symbol, DecimalPlaces, Enabled)` on every mutation.
3. EF migration + gateway route (Entity already routed). Seed the current live set on startup/first-run:
   AUD/CAD/CNY/EUR/GBP/USD (2 dp) — idempotent seeder like ChartOfAccountsSeeder.
4. Admin `Currencies.razor` (`/currencies`): list, add, edit (name/symbol/decimals), enable/disable. Nav entry.
5. Tests: domain invariants; endpoint CRUD + event; admin page renders + add/toggle.

### Phase 2 — Enforce the registry on input (currency_2)
6. Catalog consumes `CurrencyChanged` → local `SupportedCurrency` read model (mirror of enabled codes +
   decimals), same projection pattern as `StorefrontLedgerAccounts`.
7. `Storefront.ConfigureCommerce` + product/offer pricing reject a currency not registered+enabled (domain
   guard using the projection). Ordering checkout already rejects cross-currency carts; add the registry check.
8. Currency pickers in Admin (storefront commerce config, offers, pricing) are driven by the projection —
   only registered+enabled currencies are offered. Repoint tests that hard-code currency strings.

### Phase 3 — Variable-decimal money (currency_3) — the big one
9. Shared `Money.Format(minorUnits, currencyCode, decimals)` + `Money.ToMinor(text, decimals)` in
   BuildingBlocks, keyed on the currency's decimal places (resolved from the projection / a small Admin
   lookup fed by the registry). One helper, one rounding rule.
10. Replace every `/100m` display and ×100 parse with the helper across the 12 C#/razor files (Financials,
    Dashboard, Ledger, MissionControl, Orders, Subscriptions, RmaQueue, Catalog, CommerceOps, Offers, …) and
    the Next.js storefront money formatters + the provider parse sites (PayPal `ToMinor`, etc.).
11. Verify per-currency correctness: a JPY amount (0 dp) shows ¥1500 not ¥15.00; KWD (3 dp) shows 3 places.
    Ledger/tax proportional math operates on minor units already — decimal-agnostic, no change.

### Phase 4 — Docs + end-to-end tests (currency_4)
12. ADR for the currency registry + variable-decimal money; wiki. Admin nav + help.
13. E2E: add a 0-decimal currency in the admin → a storefront can select it → an order sells → it appears on
    Dashboard/Financials/Mission Control with correct (0-decimal) formatting; a disabled currency can't be
    selected for a new storefront.

## RISKS / NOTES
- **Biggest risk is Phase 3** — the `/100m` assumption is pervasive; a shared helper + mechanical replace is
  the safe path, but every money surface must be covered or amounts render 100× off. Do it test-first per file.
- Decimals for *stored* minor units don't change historical data — a USD "cent" amount stays a cent; only the
  divisor at display/parse changes. No data migration for existing amounts.
- Disable is forward-only: a disabled currency still displays wherever it has history (dashboards are
  data-driven); it just can't be chosen for new storefronts/prices.
- Confidence: P1 high, P2 medium, P3 medium-low (breadth), P4 high.
