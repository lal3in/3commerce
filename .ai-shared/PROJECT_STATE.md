# Project State

Where 3commerce stands. Read on demand — `AGENTS.md` holds the rules, this file holds the status.
Execution status per task lives in `.ai-shared/plans/plan_status_executions.md`; decisions in
`docs/adr/adr_index.md`. Update this file when a milestone lands, not per task.

Last updated: 2026-11-27 (`develop` = `test` = `main` = `6c290572`, #354 — everything merged is released)

## Built

- **MVP (Phases 1–4)** on dev/test rails: custom auth, catalog + search, cart + checkout saga,
  append-only double-entry ledger, provider-abstracted payments (mock for keyless dev, Stripe
  sandbox), refunds, Fulfillment shipments, Support + RMA saga (single refund path), Xero summary
  journals (behind `IXeroClient`; the registered client is still `LoggingXeroClient` — the real
  OAuth2 client is a launch-gate swap). PRD conformance A−→A (`docs/reviews/prd-vs-implementation.md`).
- **Post-MVP backlog BL-1..BL-11**: guest→account, admin catalog CRUD, admin Orders + storefront
  account screens, NFR-2/5/7 asserted by tests, per-line server-derived RMA, app-tier Dockerfiles,
  BL-11 dev-secret launch gate.
- **Containerized launch (ADR-0021)**: `scripts/launch.sh` over `docker-compose.yml` (EF-bundle
  migrator); Helm chart `deploy/helm/3commerce`, `kind`/CI-validated (+ kubeconform of the dev and
  prod values, incl. the observability objects); optional PgBouncer (ADR-0032).
- **Multi-tenant platform (2026-07)**: strict multi-tenancy + RLS, storefront lifecycle with
  per-storefront currency/tax (ADR-0038), per-currency shelf prices, product-status public gating,
  payment provider registry + fail-closed modes (ADR-0039), payment accounts / supplier payouts /
  webhook-secret registry, MFA (TOTP + tenant policy), cross-service audit projection, the CLI with
  real auth, analytics + content publishing, Mission Control live bus stats, and the extracted
  Marketing / Pricing / Audit / Workflow / Entitlement / Usage services (ADR-0030) — 13 DB-owning
  services in total (`scripts/lib/services.sh`).
- **Platform plumbing**: two-lane messaging — RabbitMQ bus + Kafka durable stream lane (ADR-0034);
  self-hosted Valkey for sessions, rate limiting and idempotency (ADR-0044); currency registry with
  variable-decimal money (ADR-0046); per-currency, per-storefront ledger attribution (ADR-0040/0045);
  per-store order costs, chargebacks and estimated margin (ADR-0041); scheduled job manager.
- **Supply + suppliers (2026-08)**: Offers, inventory, carriers, dropship (ADR-0028); carrier and
  product-type policy (ADR-0042); storefront-scoped carriers/payments + go-live gate (ADR-0043);
  Admin Suppliers page, supplier self-service with approval lock, storefront-scoped windowed offer
  prices (ADR-0047), approval-gated availability (ADR-0048), warehouse collect (ADR-0049),
  per-country ship-to rules (ADR-0050).
- **Pricing & promotions (2026-09)**: storefront discount (ADR-0053), threshold promotions (ADR-0051), coupon
  codes (ADR-0052), cart-preview parity (ADR-0054), discounted refund basis + storefront-scoped tax (ADR-0055),
  refunded orders keep their redemption (ADR-0056), renewal price + introductory promotions (ADR-0057);
  checkout enforces the storefront currency and Catalog's offer gate, one offer decides a line's price,
  supply and cost (ADR-0059).
- **Messaging correctness (2026-10)**: one receive endpoint per service — no more competing consumers
  (ADR-0060, #288) with the `tools/order-confirmed-backfill` operator tool for what was split; services
  bind their port only once their bus is up (ADR-0061); order-partitioned consumers; outbox delivery
  under READ COMMITTED.
- **Subscriptions + usage billing (2026-10/11)**: renewals priced for the whole line (qty × price) and
  taxed (exclusive regimes add tax); unsettled/3DS renewals go PastDue with dunning retries on days 1/3/7
  and a storefront complete-payment page; renewal and usage charges are payments (refundable by charge
  reference, partial refunds, disputes reverse their own tax; refund from the admin renewal history);
  usage auto top-ups charge the default saved card off-session (add-a-card email when there is none)
  and an unpaid usage charge has an on-session pay page; saved cards with a billing address;
  `PaymentCustomers` and saved cards under FORCE RLS; refunds require `payment.refund` via the PEP.
- **Tax invoicing phase 0 (ADR-0063, 2026-10/11)**: per-line tax breakdown behind `ITaxStrategy` (P1);
  EU OSS-aware strategy with a versioned EU-27 rate table (P1b) + follow-ups — OSS renewals at the
  destination rate, special territories / Northern Ireland, per-line destination for mixed carts,
  threshold crossing, one OSS liability account per Member State; seller tax identity (P2); bill-to +
  business buyer identity (P3/P3b); renewal/usage tax + settled-charge events (P4/P4b); invoicing facts
  on `OrderConfirmed` (P5); manual services taxed where performed.
  Not started: invoicing phase 1 (the gapless tax-invoice / credit-note series and PDFs, ADR-0063 § 11) — designed, no tracker rows yet.
- **Payments provider resolution (2026-11)**: the checkout settlement account resolves in the order's
  tenant, not the host default (#352); renewals and usage charges deliberately stay on the host-default
  provider (owner decision 2026-11-26, ADR-0039 amendment, #353).
- **Observability (2026-10/11)**: release identity on every service's telemetry and image (ADR-0064);
  self-hosted LGTM backend + collector, Faro receiver, 12-month security logs, email alerting, tail
  sampling (ADR-0065); .NET DB/messaging/runtime instrumentation, Blazor portals, Kafka traceparent;
  anonymous storefront RUM via the `/rum` relay + SSR tracing (ADR-0066); GlitchTip error inbox
  (ADR-0067); Pyroscope continuous profiling (ADR-0068); deploy markers + SLO dashboard (ADR-0069) —
  all on by default in the dev infra set. Alert recipient lives in `deploy/observability/alerting.env`
  (placeholder `test@test.com` until the owner names the real address).
- **Tooling + release flow**: feature → `develop` → `test` → `main` with fast-forward promotions
  (ADR-0058, `scripts/promote.sh`, `scripts/pr-merge-on-green.sh`); all-or-nothing local infra
  (`scripts/lib/infra.sh`); screenshot history (`scripts/screenshots/`); orphan-safe dev stack scripts;
  self-cleaning subagent worktrees; `scripts/e2e-verify.sh` as the full regression command.

## Still deferred (launch gates)

Non-code gates from `docs/prd/3commerce/15-appendix.md` Appendix B: company registration (live
Stripe keys, Xero production org + the real OAuth2 Xero client, home EU Member State), supplier
contract, operator MFA enrollment, external pen test, live carrier/provider credentials + rotated
secrets, and a managed cloud cluster. Plus the real alert-email recipient (above).

Note: `docs/prd/3commerce/13-future-considerations.md` lags the code — MFA, the Polar adapter,
Kubernetes (Helm) and promotions are listed there but have shipped. Check the ADR index before
treating a §13 item as unbuilt.

## Open work

`grep -nE '\| (pending|in_progress|blocked) \|' .ai-shared/plans/plan_status_executions.md` — as of
2026-11-27 only the EU cross-border B2C tax items, all waiting on the owner's tax adviser:

- `eu_vat_rate_table_tedb_verification` (pending) — launch gate before EU cross-border B2C: verify every
  `EuVatRateTable.Current` row against the Commission's TEDB as of the EU go-live date.
- `tax_p1b_followups_reduced_rates` (pending) — reduced VAT rates via a Catalog product VAT category;
  needs adviser-verified reduced-rate rows.
- `tax_p1b_followups` (in_progress) — the parent row; done when the reduced-rates part is.

## Where to look

- Frontend wiki: `docs/help/index.html`; analysis: `docs/help/project-analysis.html`
- Engineering gotchas (CI, tests, money, dev stack): `docs/reference/engineering-gotchas.md`
- Observability audit and its follow-ups: `docs/reviews/observability-capability-audit.md`;
  runbook `docs/runbooks/observability.md`
