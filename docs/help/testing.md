# Testing

Three layers: .NET **unit/contract** tests, .NET **integration** tests
(Testcontainers, Docker required), and **Playwright E2E** in a real browser
(storefront + admin). `scripts/e2e-verify.sh` ties them into one regression
command, and `.github/workflows/ci.yml` runs them in CI.

## Quick reference

```bash
# Unit + contract (fast, no Docker)
dotnet test 3commerce.sln --filter Category!=Integration

# Integration (Testcontainers — Docker/colima must be running)
dotnet test tests/3commerce.IntegrationTests --filter Category=Integration

# Storefront + admin E2E (needs the stack up: storefront :3000, admin :5200, gateway :8080)
cd src/Storefront && npm run test:e2e

# Full regression (automated suites only)
scripts/e2e-verify.sh

# Full regression + boot the stack and run live smoke + browser E2E
scripts/e2e-verify.sh --live
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

In CI this is the separate **integration** job.

## 3. Playwright E2E (storefront + admin)

Config: `src/Storefront/playwright.config.ts` — two projects driven by Chromium
against a **running stack**:

| Project | Dir | Base URL (env) |
|---------|-----|----------------|
| `storefront` | `src/Storefront/e2e` | `STOREFRONT_URL` (`http://localhost:3000`) |
| `admin` | `src/Storefront/e2e-admin` | `ADMIN_URL` (`http://localhost:5200`) |

Both also read `GATEWAY_URL` (`http://localhost:8080`) for API seeding/assertions.

```bash
cd src/Storefront
npm install                          # first time
npx playwright install chromium      # first time
npm run test:e2e                     # both projects
npm run test:e2e -- --project=storefront   # just the storefront
npm run test:e2e:headed              # headed (debugging)
```

### Storefront specs (`e2e/`)

- **`browse.spec.ts`** — home shows featured + categories; search is typo-tolerant
  (`?q=hedphones` still returns headphones); header search navigates to results;
  product detail renders price/variants/add-to-cart.
- **`cart-checkout.spec.ts`** — add to cart and see it listed; **full guest checkout
  end to end** (fill address → get/select shipping rate → authorize/place order →
  confirmation → **Complete test payment** → "Thank you / order confirmed");
  empty-cart state.
- **`auth.spec.ts`** — unauth `/account` redirects to login; register → log in →
  reach account; wrong password shows an error.

### Admin specs (`e2e-admin/`)

- **`admin.spec.ts`** — unauth redirects to `/login`; login reaches the dashboard
  and every nav page renders; **operator approves an RMA and the refund completes**
  (`RefundIssued`) with a balanced ledger reversal.
- **`helpers.ts`** — `loginAsAdmin` (real Blazor form + antiforgery),
  `seedPaidOrderWithRma` (seeds a paid order + open RMA via the gateway so the UI
  test can focus on approve → refund), and `rmaState`.

## 4. `scripts/e2e-verify.sh` — the regression command

This is the single "did anything break?" script.

```bash
scripts/e2e-verify.sh            # automated suites only (A1–A8)
scripts/e2e-verify.sh --live     # ALSO boot the stack and run live flows (L1–L20)
scripts/e2e-verify.sh --live-only
```

**Automated group (A1–A8):** A1 build with 0 warnings · A2 `dotnet format`
clean · A3 unit/contract · A4–A6 integration · A6b/c/d ledger/money/RMA/fulfillment ·
A6e Xero builder · A7 storefront `tsc` + `next build` · A8 vulnerable-package scan.

**Live group (L1–L20, `--live`):** boots infra, applies migrations, builds, starts
the services + worker, the storefront (production build), and the admin DLL, then:

| Checks | What |
|--------|------|
| L1–L4 | Infra (6 DBs) + service health + gateway routing (ping-pong to worker; internal health blocked) |
| L5–L8 | Auth lifecycle: register no-enumeration, verify-email, login cookie, `/me` 200/401, add address |
| L9–L13 | Catalog RBAC, import, exact + **typo** + filtered search, search p95 < 500 ms, logout, password reset |
| L14 | Storefront SSR: home/search/product render; `/account` redirects (307) |
| L15–L19 | Money flow: add to cart → checkout saga → simulate payment → confirmed → balanced ledger → admin refund → balanced reversal |
| L20 | **Storefront + Admin Playwright E2E** in a real browser (the specs above) |

It prints a pass/fail summary and exits non-zero on any failure. The
`mvp-walkthrough.md` runbook is the manual equivalent of the L-flows.

## 5. How CI runs it

`.github/workflows/ci.yml` (on push to `main`/`develop` and on PRs) has four jobs:

| Job | Runs |
|-----|------|
| **build-test** | restore → build → `dotnet format --verify-no-changes` → unit tests (`Category!=Integration`) → vulnerable-package scan |
| **integration** | `dotnet test tests/3commerce.IntegrationTests --filter Category=Integration` (Testcontainers) |
| **browser-e2e** | Installs dotnet-ef, storefront deps + Playwright Chromium; builds; runs `scripts/e2e-verify.sh --live-only` (boots stack, L1–L20). Sets `Importer__TargetRows=400` to keep the catalog import small on the 2-vCPU runner; uploads Playwright traces on failure. |
| **docker** | `docker build` of every per-service / gateway / worker Dockerfile (matrix). |
