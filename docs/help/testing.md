# Testing

Three layers: .NET **unit/contract** tests, .NET **integration** tests
(Testcontainers, Docker required), and **Playwright E2E** in a real browser
(storefront + admin + supplier portal). `scripts/e2e-verify.sh` ties them into one regression
command, and `.github/workflows/ci.yml` runs them in CI. For demos and buyer-facing
proof points, use this page together with [Selling information](./selling-information.md).

## Quick reference

```bash
# Unit + contract (fast, no Docker)
dotnet test 3commerce.sln --filter Category!=Integration

# Integration (Testcontainers — Docker/colima must be running)
dotnet test tests/3commerce.IntegrationTests --filter Category=Integration

# Storefront + admin + supplier portal E2E
# needs the stack up: storefront :3000, admin :5200, supplier :5300, gateway :8080
cd src/Storefront && npm run test:e2e

# Full regression (automated suites only)
scripts/e2e-verify.sh

# Full regression + boot the stack and run live smoke + browser E2E
scripts/e2e-verify.sh --live

# Verify the storefront go-live readiness gate against a running stack
scripts/verify-golive-gate.sh
```

> Set `DOTNET_ROOT=$HOME/.dotnet` and put `~/.dotnet`/`~/.dotnet/tools` on `PATH`
> (`.envrc` does this). The regression rule in `AGENTS.md` requires
> `scripts/e2e-verify.sh` (and the COVERAGE CHECKLIST header) to stay in sync
> whenever a test is added/removed/renamed.

## 1. Unit + contract tests

```bash
dotnet test 3commerce.sln --filter Category!=Integration
```

Per-service `tests/` projects. Covers things like the Identity password hasher and
tokens, message-contract equality, and the **Xero journal builder** (groups by
account, nets to zero, skips empty days). In `e2e-verify.sh` this is check **A3**;
in CI it is the **Unit tests** step.

**Pricing, promotions and coupons** get their own focused stage, **A3b**, which re-runs
`PromotionTests` · `PromotionEvaluatorTests` · `PricingTests` · `CouponTests` on their own so a
money-path regression is named rather than buried in the full unit run
([ADR-0051](../adr/0051-threshold-promotions-and-combinability.md) /
[ADR-0052](../adr/0052-coupon-codes-and-redemption-limits.md)):

| Suite | Guards |
|---|---|
| `Catalog/tests/PromotionTests.cs` | Aggregate invariants — at least one threshold (automatic promotions only), at least one reward, percent XOR fixed amount, scope↔product binding, ordered inclusive window, activate/deactivate; **coupons**: code normalized to trimmed UPPERCASE, bad characters/over-length rejected, a code-gated promotion may carry no threshold while an automatic one may not, clearing the code of a thresholdless coupon refused, usage limits null = unlimited and ≥ 1 when set |
| `Catalog/tests/StorefrontDiscountTests.cs` | `SetDiscount` range bounds, null leaves the current value, duplication carries the discount |
| `Ordering/tests/PromotionEvaluatorTests.cs` | Threshold AND, storefront vs product scope bases, fixed amount clamped to its scope base, best-exclusive vs Σ-combinables by customer benefit, tie → combinable set, ascending-id tiebreak, no-FX currency guard, window bounds, largest-remainder per-line allocation summing exactly |
| `Ordering/tests/PricingTests.cs` | Engine/checkout parity, storefront-wide discount (items only, stacking, subtotal cap), tax on the discounted base in both ADR-0038 regimes |
| `Ordering/tests/CouponTests.cs` | The code gate (a code-gated promotion applies only on a trimmed, case-insensitive match; an automatic one is untouched by any entered code), a coupon out-competed by a better promotion loses, one fact per `CouponStatus`, and the `u:{userId}` / `e:{email}` customer-key rule that makes a guest checkout count |

## 2. Integration tests (Testcontainers)

```bash
dotnet test tests/3commerce.IntegrationTests --filter Category=Integration
```

Spins up **real Postgres + RabbitMQ** via Testcontainers and uses the MassTransit
test harness — so **Docker must be running**. These cover the distributed spine and
business invariants end-to-end *in process* (no gateway/browser). From the
`e2e-verify.sh` COVERAGE CHECKLIST (A4–A6e):

| Check | Covers |
|-------|--------|
| A4 | Spine: outbox atomicity, durable redelivery, inbox idempotency |
| A5 | Identity auth: register no-enumeration, logout revocation, `/me` requires claims, wrong password rejected, reset revokes sessions |
| A6 | Catalog: import ≥10k SKUs, exact search, **typo fallback**, filters, search p95 < 500 ms, hostile-input safety |
| A6b | Ledger invariant: balanced entry commits, unbalanced rejected, append-only (UPDATE/DELETE blocked) |
| A6c | Money flow: guest checkout saga → confirmed + balanced sale, duplicate webhook = one entry, refund reverses + ledger balanced |
| A6d | RMA saga: approve → refund → RefundIssued, double-approve no-op, deny path; Fulfillment shipments grouped by source, idempotent |
| A6e | (unit) Xero journal builder |
| A6c (promotions) | `MoneyFlowTests`: free shipping above the money threshold + the below-threshold control, a product-scoped discount touching only that product's lines, a threshold measured on the **offer-resolved** price rather than the catalog price, combinables out-scoring a bigger exclusive, and a promotion stacking with the storefront-wide discount with tax on the doubly-discounted base — **trial balance 0 in every case** |
| A6c (projection) | `PromotionProjectionTests`: `PromotionChanged` → `PromotionCopy` insert, idempotent re-consume (no duplicate row), deactivation |
| A6c (coupons) | `CouponRedemptionTests`: the code is required for the discount and is actually charged; the cap holds under **ten concurrent checkouts against `MaxRedemptions = 3`** (exactly 3 win, counter matches the redemption rows); the per-customer limit counts a guest by checkout email across a fresh browser/casing; a failed payment **releases** the hold so a single-use code is spendable again (and is genuinely blocked while held); redelivered `CheckoutCompleted`/`OrderCancelled` neither double-confirm nor double-release; every refusal reports its own reason on `/cart/summary` and at checkout; a reservation whose checkout attempt never committed is swept so the cap recovers |
| A6c (preview parity) | `PromotionMatrixTests` + `PromotionPreviewTests`: the discount × promotion × shipping matrix with **preview and charge on the same cart** — the free-shipping/cash-discount race at a real carrier rate, the provisional path, an all-digital cart scoring free shipping at 0, exclusives never summing, a tie, an unmet threshold, and a stack capped at the subtotal (ADR-0054) |
| A6c (scope + cap) | `PromotionScopeAndCapTests`: the **per-customer limit under eight concurrent checkouts** — the only read-then-write window in the redemption path, held by an advisory lock rather than a conditional UPDATE; a storefront-scoped promotion discounting its own store and no other while an all-storefront one reaches every store of its currency; and a store-wide discount plus a promotion **jointly capped at the subtotal** (goods free, never negative, shipping still charged, trial balance 0) |
| A6c (allowance) | `CouponAllowanceTests`: a hold stranded by a crash no longer locks that shopper out forever (while an in-flight hold still refuses the second try), a reward worth 0 burns no allowance and the next shopper still gets the code, and the **persisted** per-line discount is read back out of the database (ADR-0055/0056) |
| A6c (refund policy) | `CouponRefundPolicyTests`: a confirmed redemption survives a full refund, a chargeback and a partial refund — spent for good, because the allowance rations the discount and not the revenue (ADR-0056) |
| A6c (renewals) | `SubscriptionRenewalPriceTests`: a real verified-member subscription checkout end to end into Payments — an introductory promotion charges 1500 now and **2000 on renewal**, a flagged one charges 1500 forever, and the store-wide discount never rides a renewal; the cart preview reports the same ongoing price (ADR-0057) |
| A6c (tax scope) | `StorefrontTaxScopingTests`: two live storefronts sharing one currency at 0% exclusive and 25% **inclusive** each charge their own rate AND their own regime; another tenant's store is not a tax source; a not-live storefront is refused rather than sold untaxed (ADR-0055) |
| A6d (refund basis) | `RmaRefundBasisTests`: a partial return refunds the line's **discounted** value (2800, not the 4000 it was listed at), a full return of a discounted order reaches `RefundIssued` instead of stranding in `RefundPending`, a pre-ADR-0055 snapshot still refunds but never above the captured gross, and a refund Payments cannot cover ends the RMA in a terminal `RefundFailed` (ADR-0055) |

In CI this is the separate **integration** job.

**Redis fast-path (ADR-0044).** Additional integration tests exercise the self-hosted Valkey/Redis
features against a Redis testcontainer: the distributed rate-limit store (**two instances share one
window** — the per-instance-limiter regression guard), webhook dedupe (positive cache + Postgres
backstop), and the session introspection cache — verified **on and off**, including cross-instance
sharing and **eviction on logout, role change (ClaimsVersion), and password reset**. All of it degrades
to Postgres/in-process when Redis is unavailable, and the cache is **off by default**, so the standard
suites run with it disabled (behaviour unchanged).

## 3. Playwright E2E (storefront + admin + supplier portal)

Config: `src/Storefront/playwright.config.ts` — three projects driven by Chromium
against a **running stack**:

| Project | Dir | Base URL (env) |
|---------|-----|----------------|
| `storefront` | `src/Storefront/e2e` | `STOREFRONT_URL` (`http://localhost:3000`) |
| `admin` | `src/Storefront/e2e-admin` | `ADMIN_URL` (`http://localhost:5200`) |
| `supplier` | `src/Storefront/e2e-supplier` | `SUPPLIER_URL` (`http://localhost:5300`) |

The suites also read `GATEWAY_URL` (`http://localhost:8080`) for API seeding/assertions where needed.

```bash
cd src/Storefront
npm install                          # first time
npx playwright install chromium      # first time
npm run test:e2e                     # both projects
npm run test:e2e -- --project=storefront   # just the storefront
npm run test:e2e -- --project=admin        # just the admin console
npm run test:e2e -- --project=supplier     # just the supplier portal
npm run test:e2e:headed              # headed (debugging)
```

### Storefront specs (`e2e/`)

- **`browse.spec.ts`** — home shows featured + categories; search is typo-tolerant
  (`?q=hedphones` still returns headphones); header search navigates to results;
  product detail renders price/variants/add-to-cart.
- **`catalog-product-types.spec.ts`** — when `.run/dev-dummy-data/fixtures.json` exists,
  verifies deterministic scenario product PDPs across physical, dropship, variant,
  digital, subscription, usage, and manual-service products, plus private/unpublished
  search negative coverage. Run `scripts/dev-dummy-data.sh --profile full` against a
  live stack to generate the fixture manifest.
- **`cart-checkout.spec.ts`** — add to cart and see it listed; **full guest checkout
  end to end** (fill address → get/select shipping rate → authorize/place order →
  confirmation → **Complete test payment** → "Thank you / order confirmed");
  empty-cart state.
- **`storefront-discount.spec.ts`** — the storefront-wide **Discount (n%)** row appears on the cart
  and checkout summaries and reduces the items total (never shipping or tax).
- **`storefront-promotions.spec.ts`** — a shopper crossing a threshold sees the `Promotion: {name}`
  row and the **Free shipping** line in both summaries (ADR-0051).
- **`storefront-coupons.spec.ts`** — the coupon box is invisible until a code is entered; an unknown
  code shows **its own** reason; a valid code adds the discount row; **Remove** prices the cart back
  at full price (ADR-0052).
- **`auth.spec.ts`** — unauth `/account` redirects to login; register → log in →
  reach account; wrong password shows an error.

### Admin specs (`e2e-admin/`)

- **`admin.spec.ts`** — unauth redirects to `/login`; login reaches the dashboard
  and every nav page renders; **operator approves an RMA and the refund completes**
  (`RefundIssued`) with a balanced ledger reversal.
- **`operations.spec.ts`** — broad operator-surface render checks for Catalog,
  Offers, Orders, Commerce Ops, Payment Accounts, Supplier Payouts, Xero Mappings,
  Mission Control, plus RMA action availability for a requested RMA.
- **`promotions-admin.spec.ts`** — authoring a threshold promotion through the admin modal
  (round-tripped via the API, then deactivated), and **coupon** authoring through the same modal:
  the code round-trips in canonical UPPERCASE, the **Redemptions** column renders, no threshold is
  required for a code-gated promotion, and a duplicate code is refused.
- **`commerce-ops-discount.spec.ts`** — setting the storefront-wide **Discount %** on Commerce ops
  and seeing it in the table's Discount column.
- **`helpers.ts`** — `loginAsAdmin` (real Blazor form + antiforgery),
  `seedPaidOrderWithRma` (seeds a paid order + open RMA via the gateway so the UI
  test can focus on approve → refund), and `rmaState`.

### Supplier specs (`e2e-supplier/`)

- **`supplier.spec.ts`** — unauthenticated redirect to supplier sign-in, login,
  readiness check, stock-feed request, and supplier change-request submission.

## 4. `scripts/e2e-verify.sh` — the regression command

This is the single "did anything break?" script.

```bash
scripts/e2e-verify.sh            # automated suites only (A1–A8)
scripts/e2e-verify.sh --live     # ALSO boot the stack and run live flows (L1–L20)
scripts/e2e-verify.sh --live-only  # ONLY the live group (skip A1–A8) — what CI's browser-e2e runs
```

The live group seeds the **full demo profile** (`scripts/dev-dummy-data.sh --profile full`) after the
L5–L13 auth/catalog smoke — multi-currency storefronts, the Demo Supplier, scenario products, and
attributed orders/ledger — so the browser specs run against realistic data (e.g. the ship-to and supplier
specs need a published storefront and a supplier). The seed is **idempotent** (contacts, change-requests,
and offers are guarded on their unique keys), so a re-run doesn't pile up duplicate rows.

**Automated group (A1–A8):** A1 build with 0 warnings · A2 `dotnet format`
clean · A3 unit/contract · **A3b promotions + coupons** · A4–A6 integration ·
A6b/c/d ledger/money/RMA/fulfillment · A6e Xero builder · A7 storefront `tsc` +
`next build` · A8 vulnerable-package scan.

**Live group (L1–L20, `--live`):** boots infra, applies migrations, builds, starts
the services + worker, the storefront (production build), the admin DLL, and the supplier portal DLL, then:

| Checks | What |
|--------|------|
| L1–L4 | Infra (13 DBs) + service health + gateway routing (ping-pong to worker; internal health blocked) |
| L5–L8 | Auth lifecycle: register no-enumeration, verify-email, login cookie, `/me` 200/401, add address |
| L9–L13 | Catalog RBAC, import, exact + **typo** + filtered search, search p95 < 500 ms, logout, password reset |
| L14 | Storefront SSR: home/search/product render; `/account` redirects (307) |
| L15–L19 | Money flow: add to cart → checkout saga → simulate payment → confirmed → balanced ledger → admin refund → balanced reversal |
| L20 | **Storefront + Admin + Supplier Playwright E2E** in a real browser (the specs above) — including promotion + coupon authoring in admin and the shopper applying/removing a coupon at checkout |

It prints a pass/fail summary and exits non-zero on any failure. The
`mvp-walkthrough.md` runbook is the manual equivalent of the L-flows.

## 5. `scripts/verify-golive-gate.sh` — the go-live readiness gate check

Focused runtime check for the **storefront go-live readiness gate** (ADR-0043):
a storefront selling online can't be activated until Payments and Fulfillment
have projected the required signals into Catalog. It drives a **running** dev
stack through the whole cross-service path, API-first (no direct DB access), and
exits non-zero on any failed assertion.

```bash
scripts/verify-golive-gate.sh                          # against localhost:8080
scripts/verify-golive-gate.sh --gateway http://host:8080
```

What it proves, in order:

| Step | Expectation |
|------|-------------|
| Create a Public storefront (Draft) → add canonical domain → `preview` | 200 |
| `activate` with **no** active payment account | **400** — blocked, reason names the missing payment account |
| Create → `submit` → `activate` a payment account (real Payments admin path) | 200 |
| Poll `activate` until the readiness signal reaches Catalog over the bus outbox | flips **400 → 200** (goes live) |
| Final storefront state | **Active** |

This is the **live-stack companion** to the in-process
`StorefrontReadinessCrossServiceTests` integration test (check A4 spine
coverage). Both guard the same regression: a readiness publisher that stages its
event in the EF **bus outbox** but never flushes it (a `Publish` with no
following `SaveChanges`) strands the message, so the signal never reaches
Catalog and **every** go-live silently stays blocked. See
[ADR-0043](../adr/0043-storefront-scoped-carriers-payments-and-go-live-gate.md),
the [go-live gate in Admin operations](admin-operations.md), and the
[runtime architecture](runtime-architecture.html) map.

## 6. Testing payments without a provider (mock / sandbox modes)

Payments runs in one of three modes (`Payments:Mode`, numeric on the wire —
`LocalMock=1`, `Sandbox=2`, `Production=3`; see ADR-0039). Dev defaults to
`LocalMock` in `appsettings.Development.json`, so you can exercise the full
checkout → payment → ledger flow **before** you have any provider credentials.

| Mode | External calls | Test email | Use when |
|------|----------------|-----------|----------|
| **LocalMock** | none | yes (TEST ONLY / MOCK PAYMENT, redacted payload) | no provider access yet |
| **Sandbox** | provider test endpoints | yes (same TEST-ONLY email) | you have sandbox/test credentials |
| **Production** | live provider | **never** | real transactions only |

**LocalMock behaviour.** `MockEmailPaymentProvider` simulates each outcome —
success, failure, declined card, expired card, 3DS-required, cancelled (the
`MockScenario` enum) — and on every authorize/refund publishes a
`MockPaymentCaptured` event that the Notifications worker renders as an email
to `Payments:MockEmailTo`, containing the **redacted** payload that would have
gone to the provider (never PAN/CVV/wallet tokens). Set it up with:

```jsonc
// appsettings.Development.json (Payments)
"Payments": { "Mode": "LocalMock", "AllowMockEmail": true, "MockEmailTo": "you@example.com" }
```

In the bare-run dev stack the email lands in the Notifications worker log
(`.run/notifications.log`) since dev has no SMTP; swap `MockEmailTo` for your
real address once SMTP is wired.

**Safety.** `PaymentModeGuard` refuses to boot with `LocalMock` or
`AllowMockEmail=true` outside `Development`, and Production has no capture path
at all — the TEST-ONLY email can never fire against live data. Provider secrets
are prefix-asserted per mode (`sk_test_` for Sandbox, `sk_live_` for
Production). See the [deployment guide](deployment.md) for the container/Helm
config keys and [ADR-0039](../adr/0039-payment-provider-architecture.md) for
the full design.

## 7. How CI runs it

`.github/workflows/ci.yml` (on push to `main`/`develop` and on PRs) has four jobs:

| Job | Runs |
|-----|------|
| **build-test** | restore → build → `dotnet format --verify-no-changes` → unit tests (`Category!=Integration`) → vulnerable-package scan |
| **integration** | `dotnet test tests/3commerce.IntegrationTests --filter Category=Integration` (Testcontainers) |
| **browser-e2e** | Installs dotnet-ef, storefront deps + Playwright Chromium; builds; runs `scripts/e2e-verify.sh --live-only` (boots stack, L1–L20). Sets `Importer__TargetRows=400` to keep the catalog import small on the 2-vCPU runner; uploads Playwright traces on failure. |
| **docker** | `docker build` of every per-service / gateway / worker Dockerfile (matrix). |
