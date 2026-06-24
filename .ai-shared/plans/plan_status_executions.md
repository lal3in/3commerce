# Plan Execution Status

Last Modified Date-Time: 2026-06-24 (Phase 4: mt4_1/1b/2/3/4/5-primitives done; mt7_1-lite done [Order.TenantId + OfferChanged→OfferCopy projection + checkout fulfilment resolution + confirm-on-order stock consumption] — unblocks mt4_2 confirm + mt4_5 grouping on real orders; mt4_5 remaining = storefront quote-selection UX; next: mt4_4b dropship / mt4_6 quote revalidation)

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
| mt1_2 | Identity tenant/principal/service-account/domain authorization foundation | Phase 1 foundation | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | User↔Principal link, per-tenant email index, default tenant + role/permission seeding, auth registration/login/reset lookups complete; identity tests green |
| mt1_3 | PDP/PEP policy engine and field-level metadata | Phase 1 foundation | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | PDP engine, Identity /internal/authz/decide API, PolicyDecisionService effective-permission resolver, BuildingBlocks PolicyDecisionClient PEP helper, and tests complete |
| mt1_4 | Tenant context propagation and PostgreSQL RLS helpers | Phase 1 foundation | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | TenantContext + accessor + RunInTenantScopeAsync + BeginTenantScopeAsync; RLS Testcontainers proofs; ServiceAccounts + Users FORCE RLS (UsersRlsPolicy migration). AuthService threads tenant context (tenant scope for register/login/reset-request; platform scope for secret-keyed introspect/verify/confirm-reset); IdentityAuthTests + non-superuser IdentityUsersRlsTests (isolation/platform/fail-closed) green. Gateway internal claims now carry tenant; Profile /me and address CRUD run tenant-scoped; Addresses have denormalized TenantId but FORCE RLS remains deferred until a dedicated address policy test is added. Sessions/EmailTokens stay secret-keyed (global TokenHash, no TenantId) by design. |
| mt1_5 | Gateway domain resolution and contextual rate limits | Phase 1 foundation | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | Config-backed host→tenant/storefront resolver added; spoofed context headers stripped; rate limiter partitions include tenant/storefront/IP |
| mt1_6 | .NET global tool CLI skeleton | Phase 1 foundation | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | Added src/Cli/3commerce.Cli global-tool skeleton, Gateway-only safety/help/context commands, and solution registration |
| mt1_7 | Dynamic admin-defined roles + permission registry | Phase 1 foundation | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | Roles remain data over code-defined permissions; role/membership changes increment Principal.ClaimsVersion and stale sessions are rejected at introspection |
| mt1_8 | Admin/CLI role + permission management surface | Phase 1 foundation | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-1-foundation.md | Added /admin/rbac APIs for permissions/roles/assignments/effective permissions, Admin RBAC page, and CLI rbac command placeholders |

### Phase 2: Entity / Supplier

Plan Path: `.ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md`

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---------|-----------|-------|--------|-----------|----------|
| mt2_1 | Entity service | Phase 2 entity/supplier | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | Added src/Services/Entity Api/Domain/Infrastructure/tests, entity_db wiring, named schema, EF outbox/inbox, RLS policy, gateway route, Dockerfile, ADR/API docs, and 3 unit tests |
| mt2_2 | Central tenant-scoped Entity model | Phase 2 entity/supplier | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | Added NaturalPerson/Company/Trust/Partnership/SoleTrader/GovernmentBody/NonProfitAssociation/Other, legal/trading names, and role profiles without high-risk identifiers |
| mt2_3 | Addresses, identifiers, contacts, relationships | Phase 2 entity/supplier | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | Added immutable/versioned addresses, ABN/ACN/GST identifiers with verification status, typed contact methods, and tenant-scoped relationships |
| mt2_4 | Duplicate detection warnings and overrides | Phase 2 entity/supplier | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | Warns on duplicate legal/trading names, identifiers, and contacts; supports reasoned override; no merge path added |
| mt2_5 | Supplier onboarding lifecycle | Phase 2 entity/supplier | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | Added Draft→PendingVerification→PendingApproval→Active plus Suspended/Archived, readiness checks for verified ABN/ACN + contact + address, and supplier API endpoints |
| mt2_6 | Supplier Portal | Phase 2 entity/supplier | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | Added src/SupplierPortal Blazor app with gateway-only session forwarding, supplier readiness view, stock feed request surface, and user/contact/bank change-request surface |
| mt2_7 | Admin/CLI entity and supplier management | Phase 2 entity/supplier | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-2-entity-supplier.md | Supplier change-request lifecycle (maker-checker) + customer↔entity link/de-link backend (Entity tests 46); Admin Entities & Suppliers page (list/create entities, start onboarding, change-request approve/reject) + nav link; CLI entity/supplier command groups (placeholder fidelity, --tenant guarded) matching rbac. Solution builds 0/0. Note: CLI real HTTP calls await CLI auth (mt1_6 follow-up); Users/Sessions FORCE-RLS still a follow-up (mt1_4) |

### Phase 3: Storefront / Catalog / Pricing / Payments

Plan Path: `.ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md`

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---------|-----------|-------|--------|-----------|----------|
| mt3_1 | Storefront lifecycle, domains, readiness, activation | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added Draft/Preview/Active/Paused/Archived lifecycle, public/private/password/invite visibility, canonical domains, readiness checks, admin endpoints, and Catalog tests |
| mt3_2 | Catalog tenant/storefront product model | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added TenantId, product identifiers, bundle components, variant barcode/parcel fields, tenant-scoped categories, storefront navigation, importer/admin tenant guards, migration/API docs, and ProductModel tests |
| mt3_3 | Publication readiness and SEO/product overrides | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added ProductPublication assignments, SEO/title/slug overrides, storefront variant visibility, fulfillment/customs fields, readiness/publish/unpublish admin endpoints, migration/API docs, and Publication tests |
| mt3_4 | Pricing and promotions | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added PricingEngine with SupplierCost/SellingPrice inputs, tax-mode seam, storefront-scoped promotions (coupon fixed/percent, product/category/storefront, bundle, free shipping), best-discount-wins, ProductCopy pricing fields, migration, and Pricing tests |
| mt3_5 | Ordering checkout model | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added CheckoutAttempt + lines before Order, storefront/tenant/campaign snapshots, order creation on payment success, per-storefront public order number sequence, status fallback, migration/API docs, and Checkout tests |
| mt3_6 | Payments payment account lifecycle | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added PaymentAccount lifecycle Draft→PendingApproval→Active/Suspended/Archived, live readiness, active checkout snapshots, tenant default/storefront override fields, provider mode snapshot, migration/API docs, and PaymentAccount tests |
| mt3_7 | Supplier bank accounts, payout instructions, payable policies | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added approved/masked/tokenized SupplierBankAccount, PayoutInstruction routing, SupplierPayablePolicy commission/cadence, SupplierPayable balanced accrual postings, chart accounts, migration/API docs, and SupplierPayable tests |
| mt3_8 | Xero/accounting mappings | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added XeroAccountMapping with tenant default + storefront/category/supplier/product override precedence, resolver, migration/API docs, and Xero mapping tests |
| mt3_9 | Admin/CLI/Storefront views | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added Admin Commerce Ops page for storefront/domain/product publication operations and policy summaries, nav link, CLI command groups for storefront/catalog/pricing/payment/payout/xero, storefront ESLint config/deps, and validation green |
| mt3_10 | Variant-aware cart + Ordering projection | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | ProductUpserted carries variants, Ordering ProductCopies project variants, cart add/update/delete keys by product+variant with qty 0 remove preserved, CheckoutAttempt/Order lines snapshot variant ids/skus, Storefront PDP/cart pass/display variants, migration/API docs, and VariantCart tests |
| mt3_11 | Customer shopping profile (name + typed address book) | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added User given/family names, PUT /me, AddressPurpose Billing/Shipping/Both + IsDefault, purpose-aware default conflict rules, migration/API docs, and CustomerProfile tests. Shopper profile remains Identity/customer-scoped and distinct from Entity legal data. |
| mt3_12 | Auth-aware single-page checkout | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Checkout now fetches profile/address book server-side, uses authenticated account email, pre-fills editable shipping/billing addresses, shows review lines with +/- live recalculation, attaches authenticated orders via UserId, and hides guest account-conversion UI for signed-in users. Also fixed checkout price revalidation to use selected variant price. |
| mt3_13 | Saved cards / card-on-file (Stripe Customer + Payment Element) | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added Payments-owned PaymentCustomer and SavedPaymentMethod, provider seams for Stripe Customer/SetupIntent/PaymentMethod lookup, customer payment-method endpoints, checkout SavePaymentMethod/SavedPaymentMethodId flow through AuthorizePayment, Stripe/Fake provider support, account/checkout saved-card UI, migration/API docs, and SavedCard tests. Card numbers remain provider-hosted (Payment Element/SAQ-A). |
| mt3_14 | Quantity-tier promotions + DiscountMinor | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added QuantityTier promotion kind with product/category scope, MinimumQuantity, fixed/percent tier discounts, best-discount-wins coverage, DiscountMinor on CheckoutAttempt/Order and lines, checkout/order response breakdowns, migration/API docs/e2e checklist, and Promotion tests. |
| mt3_15 | ITaxStrategy seam (home regime, export zero-rating) | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | Added Ordering ITaxStrategy with ZeroTaxStrategy default, HomeRegimeTaxStrategy (basis points, inclusive/exclusive math, exempt lines, discount allocation, export zero-rating), Payments tax seam now requires configured Tax:HomeCountry before applying Tax:FlatRate and zero-rates exports, checkout passes ship country through AuthorizePayment, docs/e2e checklist updated, and Tax tests green. |
| mt3_16 | Storefront PDP/cart purchase UX | Phase 3 storefront/catalog/pricing/payments | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-3-storefront-catalog-payments.md | PDP variant picker (auto for single SKU) + quantity stepper; cart +/- steppers (minus-at-1 removes via qty 0); addToCart carries quantity. tsc/lint clean. Shipping-estimate widget (postcode prompt) deferred to Phase 4 — hard dependency on the carrier quote API (mt4_10); cart still shows "calculated at checkout" until then |

### Phase 4: Shipping / Inventory / Fulfillment

Plan Path: `.ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md`

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---------|-----------|-------|--------|-----------|----------|
| mt4_1 | Fulfillment inventory locations and stock | Phase 4 shipping/inventory/fulfillment | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | InventoryLocation (linked to Entity + versioned address, kind=Warehouse/SupplierDirect/Forwarder, active/inactive) + InventoryItem by product/variant/location with on-hand feed (find-or-create SetOnHand/Adjust); QuantityReserved field reserved for mt4_2. InventoryService + /admin/inventory endpoints (AdminPolicy; supplier stock feed, dispatch/tracking stays admin v1). Migration + fulfillment OpenAPI regenerated. Tests: 10 domain unit (--filter Inventory) + 3 integration (find-or-create, cross-tenant rejection, available-excludes-inactive). RLS deferred (matches Catalog/Ordering app-level tenant filtering). |
| mt4_1b | Supply seam — Offers + supplier_type + fulfilment_type + price-off-variant | Phase 4 shipping/inventory/fulfillment | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | ADR-0028. (pt1) Shared BuildingBlocks.Contracts.Supply: FulfilmentType/SupplyCategory/BillingMode/PricingModel; OrderLineInfo + ShipmentCreated typed; Ordering dropped its local enum (OrderLine/CheckoutAttemptLine gain FulfilmentType+BillingMode, migration renames col + adds BillingMode); Fulfillment groups by FulfilmentType; Catalog Product gains ProductType; Entity SupplierOnboarding gains SupplierType. (pt2) First-class Offer aggregate in Catalog (product/variant×supplier → supply_category+fulfilment_type+price+pricing_model+priority+status, category↔type compatibility invariant) + /admin/offers endpoints + migration + 13 OfferTests. Build 0/0, format clean, unit Catalog 30/Ordering 19/Entity 46/Fulfillment 10, integration Fulfillment+MoneyFlow 7/7, no drift. Catalog OpenAPI regenerated. FOLLOW-UP (mt7_1): retire Variant.PriceMinor (cart still reads it); wire checkout to resolve the line's offer. |
| mt4_2 | Hybrid inventory reservations + movement ledger + single stock owner | Phase 4 shipping/inventory/fulfillment | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | DONE (engine): InventoryMovement ledger (purchase_in/adjustment/order_reserved/confirmed/cancelled/returned/transfer + reference_type/id); InventoryItem Reserve/Release/ConfirmReservation; ReservationService reserve/release/confirm — idempotent by (reference,type), allocates across active locations, writes movements; confirm works with or without a prior hold. OrderLineInfo gained VariantId. Tests: 5 domain + 5 integration (reserve/confirm/release/idempotent/multi-location). DONE (stock collapse): Fulfillment publishes InventoryAvailabilityChanged on every stock change (SetStock/Reserve/Confirm/Release via AvailabilityNotifier); Catalog InventoryAvailabilityConsumer mirrors it onto Variant.StockQuantity (now a read model, idempotent absolute overwrite) — Fulfillment is the single owner. +2 projection tests. DONE (confirm-on-order, mt7_1-lite part A/B): Order gained TenantId (propagated from CheckoutAttempt → OrderConfirmed.TenantId, migration); Fulfillment OrderConfirmedConsumer consumes warehouse-line stock via ReservationService.ConfirmAsync (idempotent, before shipments). +1 end-to-end integration test (OrderConfirmed → stock decremented through the bus). REFINEMENT (optional): pre-payment reservation hold (reserve at checkout / release on OrderCancelled) to prevent oversell during the payment window — not required for correctness (confirm-on-order decrements stock); activates fully once checkout stamps warehouse fulfilment (mt7_1-lite part C). |
| mt4_3 | Carrier integration model and lifecycle | Phase 4 shipping/inventory/fulfillment | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | CarrierIntegration aggregate (Fulfillment): CarrierCode {Fake/AusPost/DHL/FedEx/UPS/StarTrack/PackAndSend}, lifecycle Draft→Active→Suspended/Disabled, CredentialRef (secret-store reference only, never the secret), tenant-level (StorefrontId null) vs per-storefront override. CarrierService: configure/list/transition/MakeDefault (single default per scope)/ResolveDefault (storefront override beats tenant default). /admin/carriers endpoints (configure/list/activate/suspend/disable/default/credential). Real carrier needs CredentialRef to activate; Fake is keyless. Migration + fulfillment OpenAPI regenerated. Tests: 4 domain + 2 integration (override resolution, single-default-per-scope). Build 0/0, format clean, no drift. |
| mt4_4 | Carrier adapters: fake, Australia Post, DHL | Phase 4 shipping/inventory/fulfillment | done | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | Seam: ICarrierRateProvider/ICarrierLabelProvider/ICarrierTrackingProvider + DTOs (Parcel/RateRequest/CarrierRate/Label/Tracking). FakeCarrierProvider (keyless, all 3 seams, deterministic — rates scale with weight, cross-border costs more, service filter). AustraliaPostRateProvider + DhlRateProvider (sandbox rate placeholders behind the seam; replace with real API on credential onboarding). CarrierRegistry resolves provider by CarrierCode. ShippingQuoteService resolves the tenant/storefront default carrier (mt4_3) → rates, with Fake fallback so a quote always returns. Tests: 6 adapter unit (--filter CarrierAdapter) + 2 quote integration (Fake fallback, tenant-configured carrier). Build 0/0, format clean. (POST /api/shipping/quote endpoint lands with mt4_5/mt4_10.) |
| mt4_4b | Dropship fulfilment — supplier orders + availability feed | Phase 4 shipping/inventory/fulfillment | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | ADR-0028. dropship_profiles + supplier_product_availability; SupplierOrderRequested→Accepted→TrackingReceived→OrderFulfilled behind ISupplierOrderProvider (Fake first). No internal movements for pure dropship; availability from feed. Per-source credentials. |
| mt4_5 | Checkout shipping groups and quote selection | Phase 4 shipping/inventory/fulfillment | in_progress | .ai-shared/plans/multi-tenant-platform-expansion-phase-4-shipping-inventory-fulfillment.md | DONE (primitives): ShipmentGrouping splits an order's lines into shipment groups by source (warehouse location/dropship supplier); non-shipping (digital/service) lines excluded; combined parcel per group (weight summed across qty). ShippingQuoteService.QuoteGroupsAsync = one quote per group (one order, multiple shipments). POST /shipping/quote + /shipping/quote/groups endpoints (anonymous, Postman-testable, gateway-fronted) driving mt4_4 carriers with Fake fallback. Tests: 3 grouping unit + 2 HTTP integration. Build 0/0, format clean, OpenAPI regenerated. UNBLOCKED by mt7_1-lite: order lines now carry offer-resolved fulfilment types, so grouping runs on real orders. REMAINING: storefront quote-selection UX (call /shipping/quote per group, let the customer pick a method) + persist the chosen rate into the order total — Ordering↔Fulfillment + storefront flow. |
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

### Phase 7: Digital Supply / Entitlements / Usage / Billing

Extends ADR-0028 (supply seam mt4_1b) along the digital axis: download/license, subscription, and metered usage. Depends on mt4_1b.

| Task_ID | Task_Name | Phase | Status | Plan Path | Comments |
|---------|-----------|-------|--------|-----------|----------|
| mt7_1 | Pricing/Billing service + price models | Phase 7 digital-supply/billing | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-7-digital-supply-billing.md | prices (pricing_model one_time/subscription/usage_based/tiered, billing_period, currency) + price_tiers; finish moving price off Variant onto the Offer; retire Variant.PriceMinor |
| mt7_2 | Entitlement service + subscription products | Phase 7 digital-supply/billing | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-7-digital-supply-billing.md | customer_entitlements (subscription/license/download/api_access/service_access; active/expired/suspended/cancelled) + subscription_products; EntitlementCreated→CustomerAccessActivated |
| mt7_3 | Subscription billing (recurring) | Phase 7 digital-supply/billing | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-7-digital-supply-billing.md | billing_mode=recurring sets up a subscription at checkout (not single capture); renewals/trials/dunning behind IPaymentProvider (Fake first) |
| mt7_4 | Usage Metering service | Phase 7 digital-supply/billing | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-7-digital-supply-billing.md | usage_plans (token/transaction/request/minute/seat/storage_gb) + append-only usage_records rolled into usage_balances per period; never recompute per read |
| mt7_5 | Usage-based billing + overage | Phase 7 digital-supply/billing | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-7-digital-supply-billing.md | rate usage vs plan/tiers; UsageThresholdReached→OverageCharged/AccessLimited; balances gate access |
| mt7_6 | Digital fulfilment flows + storefront UX | Phase 7 digital-supply/billing | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-7-digital-supply-billing.md | saga fan-out (download/license issue, entitlement activation, meter open); React subscription/usage/download checkout + "my access/usage"; mixed-cart checkout |
| mt7_7 | Docs / e2e-verify / ADR currency for digital supply | Phase 7 digital-supply/billing | pending | .ai-shared/plans/multi-tenant-platform-expansion-phase-7-digital-supply-billing.md | OpenAPI for new services, docs/help, e2e ladder, ADR follow-ups; contracts/regression current |
