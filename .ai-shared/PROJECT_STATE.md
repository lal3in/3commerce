# Project State

Where 3commerce stands. Read on demand — `AGENTS.md` holds the rules, this file holds the status.
Execution status per task lives in `.ai-shared/plans/plan_status_executions.md`; decisions in
`docs/adr/adr_index.md`. Update this file when a milestone lands, not per task.

Last updated: 2026-10-03

## Built

- **MVP (Phases 1–4)** on dev/test rails: custom auth, catalog + search, cart + checkout saga,
  append-only double-entry ledger, provider-abstracted payments (mock for keyless dev, Stripe
  sandbox), refunds, Fulfillment shipments, Support + RMA saga (single refund path), Xero summary
  journals (logging client; real OAuth is a future swap). PRD conformance A−→A
  (`docs/reviews/prd-vs-implementation.md`).
- **Post-MVP backlog BL-1..BL-11**: guest→account, admin catalog CRUD, admin Orders + storefront
  account screens, NFR-2/5/7 asserted by tests, per-line server-derived RMA, app-tier Dockerfiles,
  BL-11 dev-secret launch gate.
- **Containerized launch (ADR-0021)**: `scripts/launch.sh` over `docker-compose.yml` (EF-bundle
  migrator); Helm chart `deploy/helm/3commerce`, `kind`/CI-validated; optional PgBouncer (ADR-0032).
- **Multi-tenant platform (2026-07)**: strict multi-tenancy + RLS, storefront lifecycle with
  per-storefront currency/tax (ADR-0038), per-currency shelf prices, product-status public gating,
  payment provider registry + fail-closed modes (ADR-0039), payment accounts / supplier payouts /
  webhook-secret registry, MFA (TOTP + tenant policy), cross-service audit projection, and the
  extracted Marketing / Pricing / Audit / Workflow / Entitlement / Usage services (ADR-0030) —
  13 DB-owning services in total (`scripts/lib/services.sh`).
- **Supply + suppliers (2026-08)**: Offers, inventory, carriers, dropship (ADR-0028); Admin
  Suppliers page, supplier self-service with approval lock, storefront-scoped windowed offer prices
  (ADR-0047), approval-gated availability (ADR-0048), warehouse collect.
- **Pricing & promotions (2026-09)**: storefront discount (ADR-0053), threshold promotions (ADR-0051), coupon
  codes (ADR-0052), discounted refund basis + storefront-scoped tax (ADR-0055), refunded orders keep
  their redemption (ADR-0056), renewal price + introductory promotions (ADR-0057).
- **Tooling**: screenshot history (`scripts/screenshots/`), orphan-safe dev stack scripts,
  `scripts/e2e-verify.sh` as the full regression command.

## Still deferred (launch gates)

Live Stripe/Xero + carrier credentials, external pen test, a managed cloud cluster — see
`docs/prd/3commerce/15-appendix.md`.

Note: `docs/prd/3commerce/13-future-considerations.md` lags the code — MFA, the Polar adapter,
Kubernetes (Helm) and promotions are listed there but have shipped. Check the ADR index before
treating a §13 item as unbuilt.

## Open work

`grep -E '\| (pending|in_progress|blocked) \|' .ai-shared/plans/plan_status_executions.md`

## Where to look

- Frontend wiki: `docs/help/index.html`; analysis: `docs/help/project-analysis.html`
- Engineering gotchas (CI, tests, money, dev stack): `docs/reference/engineering-gotchas.md`
