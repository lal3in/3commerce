# Plan Execution Status

Last Modified Date-Time: 2026-06-13 (Phase 1 + Phase 2 executed and validated; Phase 3 next)

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
| task_23 | Ordering/Payments message contracts | Phase 3 | in_progress | .ai-shared/plans/phase-3-money-checkout-ledger.md | |
| task_24 | Ledger domain + balance-constraint migration | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | NFR-1 |
| task_25 | ITaxStrategy + IPaymentProvider + Stripe adapter | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | |
| task_26 | Ordering cart (anonymous + merge) + projection | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | FR-3 |
| task_27 | CheckoutStateMachine saga + checkout endpoint | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | FR-5; hardest task |
| task_28 | Stripe webhook endpoint + inbox + reconciliation | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | |
| task_29 | Refund execution path (admin + saga-callable) | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | single path rule |
| task_30 | IdempotencyKeyFilter (BuildingBlocks) | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | NFR-3 |
| task_31 | Storefront cart/checkout/confirmation + guest convert | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | FR-4, FR-7 |
| task_32 | Chaos + ledger property tests | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | NFR-1/2 |
| task_33 | Order-confirmation email + docs/api updates | Phase 3 | done | .ai-shared/plans/phase-3-money-checkout-ledger.md | |
| task_34 | Fulfillment shipments + tracking flow | Phase 4 | pending | .ai-shared/plans/phase-4-operations-support-admin-xero.md | |
| task_35 | Support tickets (+ guest signed link) | Phase 4 | pending | .ai-shared/plans/phase-4-operations-support-admin-xero.md | FR-9 |
| task_36 | RmaStateMachine + admin RMA endpoints | Phase 4 | pending | .ai-shared/plans/phase-4-operations-support-admin-xero.md | FR-10 |
| task_37 | Xero OAuth + nightly journal job + refund postings | Phase 4 | pending | .ai-shared/plans/phase-4-operations-support-admin-xero.md | FR-11 |
| task_38 | Blazor Server admin app (all screens) | Phase 4 | pending | .ai-shared/plans/phase-4-operations-support-admin-xero.md | FR-12 |
| task_39 | Storefront support/RMA UI | Phase 4 | pending | .ai-shared/plans/phase-4-operations-support-admin-xero.md | |
| task_40 | Admin network posture (subdomain + IP allowlist) | Phase 4 | pending | .ai-shared/plans/phase-4-operations-support-admin-xero.md | |
| task_41 | Remaining notification templates | Phase 4 | pending | .ai-shared/plans/phase-4-operations-support-admin-xero.md | FR-13 |
| task_42 | Full MVP walkthrough + runbook | Phase 4 | pending | .ai-shared/plans/phase-4-operations-support-admin-xero.md | MVP gate |
| task_43 | ASVS L1 self-audit + dependency scan gates | Phase 4 | pending | .ai-shared/plans/phase-4-operations-support-admin-xero.md | NFR-6 |
| task_44 | docs/api completion + PRD status update | Phase 4 | pending | .ai-shared/plans/phase-4-operations-support-admin-xero.md | |

**Phase 3 notes:** No Stripe account/CLI in this environment → real `StripePaymentProvider` written but tests/dev use a deterministic `FakePaymentProvider` behind `IPaymentProvider` (ADR-0015). Payment success simulated via dev-only `/dev/simulate-payment` feeding the same webhook processor. Ledger enforces balance (deferred trigger) + append-only (triggers) at the DB level. Saga timeout uses MassTransit in-memory Quartz scheduler (no RabbitMQ plugin). Known limitation: saga must start (CartSubmitted delivered) before payment can succeed — true in practice (client confirms after checkout returns); a fully out-of-order-tolerant saga is a future hardening. IdempotencyKeyFilter implemented as a per-endpoint IdempotencyRecord on the refund endpoint rather than a generic BuildingBlocks filter.
