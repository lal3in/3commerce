# Plan Execution Status

Last Modified Date-Time: 2026-06-15 (post-MVP backlog: BL-1/3/4/5/8/9 DONE; BL-2 catalog editor next, then BL-6/7/10/11 + CI paths-filter)

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
| BL-2 | FR-12 admin catalog CRUD | review (Partial) | Blazor catalog page + create/update endpoints (only DELETE /admin/products/{id} exists today) |
| BL-3 | Admin Orders screen - real list/detail | wiki | **DONE 2026-06-15**: GET /admin/orders endpoint + Blazor Orders page (list) |
| BL-4 | Account page - order history + addresses | wiki | **DONE 2026-06-15**: account page renders order history (getMyOrders) |
| BL-5 | Storefront nav to /orders/[id]/support | wiki | **DONE 2026-06-15**: confirmation page now links to "Contact support or request a refund" |
| BL-6 | NFR-2 chaos test on the checkout saga | review (Partial) | current chaos test is on the ping-pong spine only |
| BL-7 | NFR-5/7 measure product-SSR p95 + end-to-end checkout trace | review (Partial) | wired but unasserted (search p95 IS measured) |
| BL-8 | RMA refund amount - derive from order, not free-form client input | wiki | **DONE 2026-06-15**: per-line RMA, server-derived amount. Support `OrderSnapshot` read-copy (fed by `OrderConfirmed` via `OrderSnapshotConsumer`); `GET /tickets/orders/{id}/lines`; `/rma` takes `{orderId, reason, lines[]}` (no client amount) → amount summed from snapshot, capped at purchased qty. Storefront line-selection UI. 4 RMA integration tests pass (inc. new per-line server-price assertion). |
| BL-9 | Wire STORE_CURRENCY (remove hard-coded "EUR") | review/wiki | **DONE 2026-06-15**: Store:Currency config (default EUR) in importer + cart fallback + storefront env; data model already per-entity currency (multi-currency display = future FX) |
| BL-10 | App-tier Dockerfiles (storefront + admin) | wiki | only the 6 services + gateway + worker are containerized |
| BL-11 | Rotate dev secrets per environment (ES256 key, admin pw) | review | committed DEV-ONLY; launch gate |

**Launch gates (non-code, PRD Appendix B):** company registration -> live Stripe/Xero + real ITaxStrategy; supplier contract -> real catalog feed + dropship/warehouse decision; external pen test; Kubernetes deployment track.

## Frontend E2E + CI (added after Phase 4)

| Area | Status | Notes |
|------|--------|-------|
| Storefront Playwright E2E (src/Storefront/e2e) | done | browse/search/cart/guest-checkout/account - 10 tests |
| Admin Playwright E2E (src/Storefront/e2e-admin) | done | login/pages/RMA approve->refund->RefundIssued - 3 tests |
| e2e-verify.sh L1-L20 (incl. real-browser E2E) | done | full live regression, 28 checks |
| CI browser-e2e job (boots stack, runs Playwright) | done | green on main; Importer:TargetRows=400 in CI |
| Frontend wiki + conformance review + analysis page | done | docs/help/, docs/reviews/ |
