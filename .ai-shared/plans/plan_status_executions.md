# Plan Execution Status

Last Modified Date-Time: 2026-06-18 (Generated phase-level multi-tenant expansion plans and regrouped status tasks mt1_1..mt6_12 as PLANNED; then registered storefront/admin design-grill addendum tasks mt1_7..mt1_8, mt3_10..mt3_16, mt4_10..mt4_11, mt6_13..mt6_14 as PLANNED; implementation not started)

Statuses: `pending` | `in_progress` | `done` | `blocked` | `skipped`

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---------|-----------|-------|--------|-----------|----------|
| task_1 | Solution + build props (RootNamespace gotcha) | Phase 1 | done | .ai-shared/plans/phase-1-skeleton-and-spine.md | classic .sln (not .slnx); MassTransit pinned 8.5.10 (v9 is commercial) |
| task_2 | docker-compose.infra.yml + init-databases.sql | Phase 1 | done | .ai-shared/plans/phase-1-skeleton-and-spine.md | DEVIATION: Docker Desktop daemon won't start headless → colima installed/used |
| task_3 | BuildingBlocks.Contracts (ping-pong records) | Phase 1 | done | .ai-shared/plans/phase-1-skeleton-and-spine.md | |
| task_4 | BuildingBlocks.Infrastructure (bus/OTel/web helpers) | Phase 1 | done | .ai-shared/plans/phase-1-skeleton-and-spine.md | AddServiceBus has worker overload without outbox |
| task_5 | Six service skeletons (DbContext+outbox+health) | Phase 1 | done | .ai-shared/plans/phase-1-skeleton-and-spine.md | migrations applied to all 6 DBs; /health/ready Healthy ×6 |
| task_6 | Notifications worker stub | Phase 1 | done | .ai-shared/plans/phase-1-skeleton-and-spine.md | |
| task_7 | Ping-pong spine Catalog→Ordering via outbox | Phase 1 | done | .ai-shared/plans/phase-1-skeleton-and-spine.md | one TraceId spans catalog/ordering/notifications |
| task_8 | YARP Gateway (routes, header strip, OTel) | Phase 1 | done | .ai-shared/plans/phase-1-skeleton-and-spine.md | /api/*/health* blocked (404); PHASE2 auth insertion point marked |
| task_9 | Spine integration tests (outbox/redelivery/idempotency) | Phase 1 | done | .ai-shared/plans/phase-1-skeleton-and-spine.md | 3/3 pass on Testcontainers; xunit v2 API (not v3) |
| task_10 | Dockerfiles ×8 | Phase 1 | done | .ai-shared/plans/phase-1-skeleton-and-spine.md | gateway image build verified locally |
| task_11 | CI workflow (build/format/test/docker/vuln) | Phase 1 | done | .ai-shared/plans/phase-1-skeleton-and-spine.md | not yet pushed — validate on first push |
| task_12 | AGENTS.md updates post-scaffold | Phase 1 | done | .ai-shared/plans/phase-1-skeleton-and-spine.md | status/structure/notes updated (ports, namespace map, colima) |
| task_13 | Identity domain + AuthService (Argon2id) | Phase 2 | done | .ai-shared/plans/phase-2-identity-and-catalog.md | Argon2id m=19456,t=2,p=1; sessions/tokens stored hashed |
| task_14 | Identity endpoints + internal introspection | Phase 2 | done | .ai-shared/plans/phase-2-identity-and-catalog.md | introspection single-listener+no-route (not separate port) |
| task_15 | Gateway session validation + claims minting | Phase 2 | done | .ai-shared/plans/phase-2-identity-and-catalog.md | NFR-8; removed Phase-1 RequestHeaderRemove transform (stripped legit header) |
| task_16 | BuildingBlocks internal-claims auth for services | Phase 2 | done | .ai-shared/plans/phase-2-identity-and-catalog.md | ES256 JWT, services hold public key only |
| task_17 | Catalog schema + FTS/pg_trgm migration | Phase 2 | done | .ai-shared/plans/phase-2-identity-and-catalog.md | dynamic JSON for jsonb POCO props; SearchSchema raw-SQL migration |
| task_18 | ISupplierImporter + SampleDataImporter (10k SKUs) | Phase 2 | done | .ai-shared/plans/phase-2-identity-and-catalog.md | FR-1: 10500 read/10417 accepted/83 rejected; per-row RNG for idempotency |
| task_19 | ISearchProvider + Postgres search + endpoints | Phase 2 | done | .ai-shared/plans/phase-2-identity-and-catalog.md | FR-2 typo fallback; NFR-5 p95 ~8.5ms |
| task_20 | Storefront v0 (Next.js SSR + auth UI) | Phase 2 | done | .ai-shared/plans/phase-2-identity-and-catalog.md | Next 15.5.19; SSR verified live against gateway |
| task_21 | Search perf + auth NFR tests | Phase 2 | done | .ai-shared/plans/phase-2-identity-and-catalog.md | 7 unit + 14 integration tests; serial collection fixtures |
| task_22 | docs/api contracts + index (Identity/Catalog) | Phase 2 | done | .ai-shared/plans/phase-2-identity-and-catalog.md | OpenAPI exported to docs/api/ + index |
| + Notifications email (IEmailSender + consumers) | UserRegistered/PasswordReset → email | Phase 2 | done | .ai-shared/plans/phase-2-identity-and-catalog.md | added (was implied by plan New Files); LoggingEmailSender sandbox |
| task_23 | Ordering/Payments message contracts | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | |
| task_24 | Ledger domain + balance-constraint migration | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | NFR-1 |
| task_25 | ITaxStrategy + IPaymentProvider + Stripe adapter | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | |
| task_26 | Ordering cart (anonymous + merge) + projection | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | FR-3 |
| task_27 | CheckoutStateMachine saga + checkout endpoint | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | FR-5; hardest task |
| task_28 | Stripe webhook endpoint + inbox + reconciliation | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | |
| task_29 | Refund execution path (admin + saga-callable) | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | single path rule |
| task_30 | IdempotencyKeyFilter (BuildingBlocks) | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | NFR-3 |
| task_31 | Storefront cart/checkout/confirmation | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | FR-4 done; FR-7 done via BL-1 (2026-06-15) |
| task_32 | Chaos + ledger property tests | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | NFR-1/2 |
| task_33 | Order-confirmation email + docs/api updates | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | |
| task_34 | Fulfillment shipments + tracking flow | Phase 4 | done | .ai-shared/plans/phase-4-operations-support-admin-xero.md | |
| task_35 | Support tickets (+ guest signed link) | Phase 4 | done | .ai-shared/plans/phase-4-operations-support-admin-xero.md | FR-9 |
| task_36 | RmaStateMachine + admin RMA endpoints | Phase 4 | done | .ai-shared/plans/phase-4-operations-support-admin-xero.md | FR-10 |
| task_37 | Xero OAuth + nightly journal job + refund postings | Phase 4 | done | .ai-shared/plans/phase-4-operations-support-admin-xero.md | FR-11 |
| task_38 | Blazor Server admin app (all screens) | Phase 4 | done | .ai-shared/plans/phase-4-operations-support-admin-xero.md | FR-12 |
| task_39 | Storefront support/RMA UI | Phase 4 | done | .ai-shared/plans/phase-4-operations-support-admin-xero.md | |
| task_40 | Admin network posture (subdomain + IP allowlist) | Phase 4 | done | .ai-shared/plans/phase-4-operations-support-admin-xero.md | |
| task_41 | Remaining notification templates | Phase 4 | done | .ai-shared/plans/phase-4-operations-support-admin-xero.md | FR-13 |
| task_42 | Full MVP walkthrough + runbook | Phase 4 | done | .ai-shared/plans/phase-4-operations-support-admin-xero.md | MVP gate |
| task_43 | ASVS L1 self-audit + dependency scan gates | Phase 4 | done | .ai-shared/plans/phase-4-operations-support-admin-xero.md | NFR-6 |
| task_44 | docs/api completion + PRD status update | Phase 4 | done | .ai-shared/plans/phase-4-operations-support-admin-xero.md | |

**Phase 3 notes:** No Stripe account/CLI in this environment → real `StripePaymentProvider` written but tests/dev use a deterministic `FakePaymentProvider` behind `IPaymentProvider` (ADR-0015). Payment success simulated via dev-only `/dev/simulate-payment` feeding the same webhook processor. Ledger enforces balance (deferred trigger) + append-only (triggers) at the DB level. Saga timeout uses MassTransit in-memory Quartz scheduler (no RabbitMQ plugin). Known limitation: saga must start (CartSubmitted delivered) before payment can succeed — true in practice (client confirms after checkout returns); a fully out-of-order-tolerant saga is a future hardening. IdempotencyKeyFilter implemented as a per-endpoint IdempotencyRecord on the refund endpoint rather than a generic BuildingBlocks filter.

**Phase 4 notes:** Fulfillment (shipments grouped by FulfillmentSource, unique (OrderId,Source) index for idempotency) + Support/RMA saga (Requested→Approved/Denied→AwaitingReturn→ReturnReceived→RefundIssued; reuses the single Phase-3 RefundRequested path; terminal states retained as the admin read model). Xero: no org/creds here → journal builder (pure, unit-tested) + DailyJournalJob + RefundPostingConsumer + LoggingXeroClient (real OAuth2 client a future swap, like Stripe). Blazor admin (src/Admin, :5200): cookie auth, admin-role probe (introspection is internal-only), IP-allowlist middleware, GatewayClient (no service/DB refs); pages Dashboard/Orders/RMA queue/Ledger/Xero/Imports — build-validated (browser flows untested here). Storefront support/RMA UI build-validated. OrderConfirmed enriched with line items (published by the Order aggregate owner, not the saga) so Fulfillment needs no cross-service reads. 25 integration + 11 unit tests green. ASVS L1 self-audit + MVP runbook written. Remaining launch gates (registration → live Stripe/Xero, supplier, external pen test) are non-code, per PRD Appendix B.

---

## Conformance review (2026-06-15)

Independent team review (`docs/reviews/prd-vs-implementation.md`): **grade A−** (→ A after BL-1), 16 Met / 4 Partial / 0 Missing of 21 FR/NFR. Core money + auth engine is production-grade; gaps are in the frontend/back-office and in test coverage, not the ledger. Analysis: `docs/help/project-analysis.html`. Frontend wiki: `docs/help/`.

## Post-MVP backlog (from the conformance review — next work items)

| ID | Item | Source | Notes |
|----|------|--------|-------|
| BL-1 | FR-7 guest -> account conversion | review (Missing) | **DONE 2026-06-15**: `EmailVerified` event → Ordering `GuestOrderAttachConsumer` (attach by verified email); `/convert-guest` endpoint; storefront convert form. 2 integration tests + live + E2E verified. |
| BL-2 | FR-12 admin catalog CRUD | review (Partial) | **DONE 2026-06-15**: full catalog editor. Catalog `GET/POST/PUT /admin/products` (+ existing DELETE): create/edit over variants, stock, images, attributes; variant reconcile (match/add/remove); slug-unique + category-required guards (a product without a real category is invisible to FTS); publishes `ProductUpserted`. Blazor `/catalog` editor page (list/search + form). 4 integration tests pass. |
| BL-3 | Admin Orders screen - real list/detail | wiki | **DONE 2026-06-15**: GET /admin/orders endpoint + Blazor Orders page (list) |
| BL-4 | Account page - order history + addresses | wiki | **DONE 2026-06-15**: account page renders order history (getMyOrders) |
| BL-5 | Storefront nav to /orders/[id]/support | wiki | **DONE 2026-06-15**: confirmation page now links to "Contact support or request a refund" |
| BL-6 | NFR-2 chaos test on the checkout saga | review (Partial) | **DONE 2026-06-15**: `MoneyFlowTests.Checkout_saga_survives_an_ordering_outage_during_payment` — Ordering (saga host) restarted mid-flight via new `Phase3Fixture.RestartOrderingAsync`; PaymentSucceeded queues durably and the saga still reaches Confirmed + balanced ledger. |
| BL-7 | NFR-5/7 measure product-SSR p95 + end-to-end checkout trace | review (Partial) | **DONE 2026-06-15**: NFR-5 `Product_detail_meets_p95_latency_budget` (GET /products/{slug} p95 < 500ms, the SSR-dominant fetch); NFR-7 `Checkout_emits_one_distributed_trace...` — ActivityListener proves one TraceId spans the AspNetCore entry span and MassTransit message hops. |
| BL-8 | RMA refund amount - derive from order, not free-form client input | wiki | **DONE 2026-06-15**: per-line RMA, server-derived amount. Support `OrderSnapshot` read-copy (fed by `OrderConfirmed` via `OrderSnapshotConsumer`); `GET /tickets/orders/{id}/lines`; `/rma` takes `{orderId, reason, lines[]}` (no client amount) → amount summed from snapshot, capped at purchased qty. Storefront line-selection UI. 4 RMA integration tests pass (inc. new per-line server-price assertion). |
| BL-9 | Wire STORE_CURRENCY (remove hard-coded "EUR") | review/wiki | **DONE 2026-06-15**: Store:Currency config (default EUR) in importer + cart fallback + storefront env; data model already per-entity currency (multi-currency display = future FX) |
| BL-10 | App-tier Dockerfiles (storefront + admin) | wiki | **DONE 2026-06-15**: `src/Storefront/Dockerfile` (Next.js `output: "standalone"`, non-root node runner) + `src/Admin/Dockerfile` (SDK→aspnet, mirrors services). Both added to the CI docker matrix; both verified building locally. |
| BL-11 | Rotate dev secrets per environment (ES256 key, admin pw) | review | **DONE 2026-06-15**: fail-fast launch gate — `DevSecretGuard` (services, via `AddInternalClaimsAuth`) + inline check in Gateway `InternalClaimsMinter` refuse to boot outside Development with the committed dev ES256 key (matched by SHA-256 fingerprint, not key material). `scripts/rotate-secrets.sh` emits a fresh keypair + admin password as env vars; `docs/ops/secrets.md` documents the secrets + rotation. 4 `DevSecretGuardTests` pass. |

**Launch gates (non-code, PRD Appendix B):** company registration -> live Stripe/Xero + real ITaxStrategy; supplier contract -> real catalog feed + dropship/warehouse decision; external pen test; Kubernetes deployment track.

## Frontend E2E + CI (added after Phase 4)

| Area | Status | Notes |
|------|--------|-------|
| Storefront Playwright E2E (src/Storefront/e2e) | done | browse/search/cart/guest-checkout/account - 10 tests |
| Admin Playwright E2E (src/Storefront/e2e-admin) | done | login/pages/RMA approve->refund->RefundIssued - 3 tests |
| e2e-verify.sh L1-L20 (incl. real-browser E2E) | done | full live regression, 28 checks |
| CI browser-e2e job (boots stack, runs Playwright) | done | green on main; Importer:TargetRows=400 in CI |
| Frontend wiki + conformance review + analysis page | done | docs/help/, docs/reviews/ |

---

## Plan: Containerized launch + repeatable fresh/reuse deployment (compose → Helm/k8s)

Plan Path: `.ai-shared/plans/containerized-launch-and-deploy.md`
Branch (when executing): `feat/containerized-launch` off `develop`. Status: PLANNED (not started).

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---------|-----------|-------|--------|-----------|----------|
| cl_1 | ContainerConfig.cs (flag-loaded appsettings.Container.json) | P1 config | done | .ai-shared/plans/containerized-launch-and-deploy.md | USE_CONTAINER_CONFIG=true, decoupled from env name |
| cl_2 | AddContainerConfig in 8 app Program.cs | P1 config | done | .ai-shared/plans/containerized-launch-and-deploy.md | after CreateBuilder |
| cl_3 | 8x appsettings.Container.json host overrides | P1 config | done | .ai-shared/plans/containerized-launch-and-deploy.md | postgres/rabbitmq/service names == compose==k8s |
| cl_4 | Add curl + ASPNETCORE_URLS :8080 to 8 runtime images | P1 config | done | .ai-shared/plans/containerized-launch-and-deploy.md | healthcheck needs curl |
| cl_5 | deploy/migrator (6 EF bundles, self-contained linux-x64) + entrypoint | P2 migrations | done | .ai-shared/plans/containerized-launch-and-deploy.md | worker excluded (no DB) |
| cl_6 | docker-compose.yml hardened full stack | P3 compose | done | .ai-shared/plans/containerized-launch-and-deploy.md | healthchecks/depends_on/limits/env_file |
| cl_7 | deploy/.env.dev (+ gitignore .env.prod) | P3 compose | done | .ai-shared/plans/containerized-launch-and-deploy.md | prod env_file minted by launch.sh |
| cl_8 | scripts/launch.sh [--fresh / --reuse] [--env dev / prod] | P3 compose | done | .ai-shared/plans/containerized-launch-and-deploy.md | fresh=down -v; prod mints+injects keys; verified end-to-end |
| cl_9 | deploy/helm/3commerce umbrella chart (per-service migrate initContainer) | P4 k8s | done | .ai-shared/plans/containerized-launch-and-deploy.md | dev/prod values; native Secrets; CI-validated in cl_11 |
| cl_10 | deploy/helm/make-secret.sh (prod K8s Secret) | P4 k8s | done | .ai-shared/plans/containerized-launch-and-deploy.md | base64 PEMs preserve newlines |
| cl_11 | CI: paths-filter + compose-smoke + kind-deploy jobs | P5 CI | done | .ai-shared/plans/containerized-launch-and-deploy.md | kind helm install --wait |
| cl_12 | ADR-0021 dual launch model (amends ADR-0009) | P6 docs | done | .ai-shared/plans/containerized-launch-and-deploy.md | |
| cl_13 | Full re-audit -> regenerate project-analysis.html (Atelier) | P6 docs | done | .ai-shared/plans/containerized-launch-and-deploy.md | expect ~20 Met/1 Partial/0 Missing ~A |
| cl_14 | Refresh deployment.md/html + getting-started + AGENTS.md | P6 docs | done | .ai-shared/plans/containerized-launch-and-deploy.md | keep bare-run section |
| cl_15 | Update status + e2e-verify test-list | P7 validate | done | .ai-shared/plans/containerized-launch-and-deploy.md | |

---

## Plan: Multi-tenant / multi-storefront platform expansion

Parent Plan Path: `.ai-shared/plans/multi-tenant-platform-expansion.md`
Status: PLANNED (phase plans generated; implementation not started).

### Phase 1: Foundation

Plan Path: `.ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md`

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---------|-----------|-------|--------|-----------|----------|
| mt1_1 | Architecture ADRs and scope baseline | Phase 1 foundation | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | ADRs 0023 (strict multi-tenancy), 0024 (RLS via SET LOCAL), 0025 (PDP/PEP + dynamic RBAC), 0026 (service accounts + CLI) + adr_index updated |
| mt1_2 | Identity tenant/principal/service-account/domain authorization foundation | Phase 1 foundation | in_progress | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | Decomposed: 2a domain model + registry + invariants + tests (in progress); 2b DbContext + EF migration; 2c link User↔Principal, per-tenant email, auth rewrite, default-role seeding |
| mt1_3 | PDP/PEP policy engine and field-level metadata | Phase 1 foundation | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | Central PDP, service-side PEP helpers |
| mt1_4 | Tenant context propagation and PostgreSQL RLS helpers | Phase 1 foundation | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | SET LOCAL transaction-scoped tenant context |
| mt1_5 | Gateway domain resolution and contextual rate limits | Phase 1 foundation | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | Trusted tenant/storefront context only from Gateway |
| mt1_6 | .NET global tool CLI skeleton | Phase 1 foundation | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | Human MasterGlobal broad mirror; service accounts narrow |
| mt1_7 | Dynamic admin-defined roles + permission registry | Phase 1 foundation | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | Grill addendum: roles are data over code-defined permissions; claim invalidation on change |
| mt1_8 | Admin/CLI role + permission management surface | Phase 1 foundation | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | Grill addendum: create/edit roles, assign perms, preview effective permissions |

### Phase 2: Entity / Supplier

Plan Path: `.ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md`

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---------|-----------|-------|--------|-----------|----------|
| mt2_1 | Entity service | Phase 2 entity/supplier | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | New Api/Domain/Infrastructure/tests projects |
| mt2_2 | Central tenant-scoped Entity model | Phase 2 entity/supplier | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | NaturalPerson, Company, Trust, etc. |
| mt2_3 | Addresses, identifiers, contacts, relationships | Phase 2 entity/supplier | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | Immutable/versioned addresses; ABN/ACN verification status |
| mt2_4 | Duplicate detection warnings and overrides | Phase 2 entity/supplier | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | No entity merge in v1 |
| mt2_5 | Supplier onboarding lifecycle | Phase 2 entity/supplier | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | Draft→Active readiness workflow |
| mt2_6 | Supplier Portal | Phase 2 entity/supplier | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | Generic platform-branded; stock update allowed |
| mt2_7 | Admin/CLI entity and supplier management | Phase 2 entity/supplier | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | Entity CRUD, customer link/de-link, supplier requests |

### Phase 3: Storefront / Catalog / Pricing / Payments

Plan Path: `.ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md`

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---------|-----------|-------|--------|-----------|----------|
| mt3_1 | Storefront lifecycle, domains, readiness, activation | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Draft/Preview/Active/Paused/Archived; live activation approval |
| mt3_2 | Catalog tenant/storefront product model | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Variants, identifiers, bundles, taxonomy |
| mt3_3 | Publication readiness and SEO/product overrides | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Explicit storefront publish only |
| mt3_4 | Pricing and promotions | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | SupplierCost, SellingPrice, tax mode, limited promos |
| mt3_5 | Ordering checkout model | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | CheckoutAttempt before Order |
| mt3_6 | Payments payment account lifecycle | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Tenant default + storefront override |
| mt3_7 | Supplier bank accounts, payout instructions, payable policies | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Payments-owned bank data; approval protected |
| mt3_8 | Xero/accounting mappings | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Summary journals first; detailed sync model-ready |
| mt3_9 | Admin/CLI/Storefront views | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Field-level policy on all sensitive fields |
| mt3_10 | Variant-aware cart + Ordering projection | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Grill addendum: cart line keyed by (Product,Variant); ProductCopies carries variants |
| mt3_11 | Customer shopping profile (name + typed address book) | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Grill addendum: billing(residential)/shipping(friend) + defaults; distinct from Entity |
| mt3_12 | Auth-aware single-page checkout | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Grill addendum: prefill, live-recalc, hide create-account, auto-attach order |
| mt3_13 | Saved cards / card-on-file (Stripe Customer + Payment Element) | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Grill addendum: Apple/Google/card, opt-in save, one-click; PCI SAQ-A |
| mt3_14 | Quantity-tier promotions + DiscountMinor | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Grill addendum: extends mt3_4 promo set; best-discount-wins |
| mt3_15 | ITaxStrategy seam (home regime, export zero-rating) | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Grill addendum: defaults to 0 (ADR-0015) until a home regime is configured |
| mt3_16 | Storefront PDP/cart purchase UX | Phase 3 storefront/catalog/pricing/payments | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Grill addendum: variant picker, qty steppers, shipping-estimate widget (postcode prompt) |

### Phase 4: Shipping / Inventory / Fulfillment

Plan Path: `.ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md`

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---------|-----------|-------|--------|-----------|----------|
| mt4_1 | Fulfillment inventory locations and stock | Phase 4 shipping/inventory/fulfillment | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | Locations linked to Entity/address |
| mt4_2 | Hybrid inventory reservations | Phase 4 shipping/inventory/fulfillment | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | Hard reserve during checkout/payment saga |
| mt4_3 | Carrier integration model and lifecycle | Phase 4 shipping/inventory/fulfillment | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | Tenant default + storefront overrides |
| mt4_4 | Carrier adapters: fake, Australia Post, DHL | Phase 4 shipping/inventory/fulfillment | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | Fake carrier required for tests/dev |
| mt4_5 | Checkout shipping groups and quote selection | Phase 4 shipping/inventory/fulfillment | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | One order, multiple shipment groups |
| mt4_6 | Quote expiry, fallback, and final revalidation | Phase 4 shipping/inventory/fulfillment | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | Revalidate before payment |
| mt4_7 | Shipments, packages, labels, tracking | Phase 4 shipping/inventory/fulfillment | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | Label/tracking automation default off |
| mt4_8 | Support/RMA shipment/order-line awareness | Phase 4 shipping/inventory/fulfillment | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | Partial returns and manual restock |
| mt4_9 | Order holds before fulfillment | Phase 4 shipping/inventory/fulfillment | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | Fraud/payment/address/inventory holds |
| mt4_10 | Carrier adapters: FedEx, UPS, StarTrack, Pack & Send | Phase 4 shipping/inventory/fulfillment | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | Grill addendum: extends mt4_4 (AusPost/DHL/Fake); Postman-testable /api/shipping/quote |
| mt4_11 | Per-variant weight + dims with default-parcel fallback | Phase 4 shipping/inventory/fulfillment | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | Grill addendum: carrier rate inputs; seeded SKUs fall back to a configurable default parcel |

### Phase 5: Marketing / Theme / SEO

Plan Path: `.ai-shared/plans/multi-tenant-platform-expansion-phase-5-marketing-theme-seo.md`

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---------|-----------|-------|--------|-----------|----------|
| mt5_1 | Marketing/Analytics service | Phase 5 marketing/theme/SEO | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-5-marketing-theme-seo.md | Dedicated service boundary |
| mt5_2 | Campaign targeting and attribution | Phase 5 marketing/theme/SEO | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-5-marketing-theme-seo.md | Store all touches; last-click v1 |
| mt5_3 | Short links | Phase 5 marketing/theme/SEO | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-5-marketing-theme-seo.md | Platform global short domain v1 |
| mt5_4 | Analytics event collector | Phase 5 marketing/theme/SEO | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-5-marketing-theme-seo.md | Batched JSONB append-only events |
| mt5_5 | Storefront consent and behavior tracking | Phase 5 marketing/theme/SEO | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-5-marketing-theme-seo.md | Consent-aware first-party analytics |
| mt5_6 | Storefront theme/template framework | Phase 5 marketing/theme/SEO | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-5-marketing-theme-seo.md | Shared core + templates + bespoke seam |
| mt5_7 | Draft/preview/publish/scheduled publishing | Phase 5 marketing/theme/SEO | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-5-marketing-theme-seo.md | Workflow scheduled publish commands |
| mt5_8 | SEO/agent-friendly metadata | Phase 5 marketing/theme/SEO | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-5-marketing-theme-seo.md | JSON-LD, sitemap, robots, llms.txt |
| mt5_9 | Product feeds per storefront | Phase 5 marketing/theme/SEO | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-5-marketing-theme-seo.md | Toggleable merchant/ad feeds |

### Phase 6: Audit / Workflow / Compliance

Plan Path: `.ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md`

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---------|-----------|-------|--------|-----------|----------|
| mt6_1 | Audit service and local audit framework | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | Local authoritative + central projection |
| mt6_2 | Audit coverage rules | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | Mutations, sensitive reads, high-risk denies |
| mt6_3 | Workflow service | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | Quartz + MassTransit typed jobs |
| mt6_4 | Approval orchestration | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | Identity policy + Workflow task + owning service apply |
| mt6_5 | Notifications channel abstraction and alerts | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | Email first; high-risk alerts |
| mt6_6 | Outbound tenant webhooks | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | Signed delivery/retry logs |
| mt6_7 | Inbound provider webhook routing conventions | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | Gateway routes; owning service verifies |
| mt6_8 | Import/export adapters and async export jobs | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | CSV/JSON first; sensitive exports audited |
| mt6_9 | Object storage abstraction and image variants | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | Metadata in owning services |
| mt6_10 | MFA/step-up toggle and tenant policy | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | Tenant configurable within platform minimums |
| mt6_11 | Region-aware operations and retention | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | One physical region initially |
| mt6_12 | Docs, e2e-verify, SLOs, launch gates | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | Keep contracts/ADRs/regression current |
| mt6_13 | Observability metrics stack (OTel metrics + Prometheus + Grafana) | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | Grill addendum: metrics export + dashboards (compose + Helm); today traces-only |
| mt6_14 | Admin mission-control console | Phase 6 audit/workflow/compliance | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-6-audit-workflow-compliance.md | Grill addendum: live bus view + wiretap, heartbeat/metrics dashboard, Accounts section |
