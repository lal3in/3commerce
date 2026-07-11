# PRD vs. Implementation — Conformance Review

> **Refresh 2026-07-11 — verdicts that changed since the 2026-06-15 review.** The four remaining
> Partials were closed the same week by backlog items BL-2 (FR-12 full admin catalog CRUD), BL-6
> (NFR-2 checkout-saga chaos test: Ordering restarted mid-payment, saga still reaches Confirmed +
> balanced ledger), and BL-7 (NFR-5 product-detail p95 asserted; NFR-7 single-TraceId asserted via
> ActivityListener) — code-level tally now **21 Met / 0 Partial / 0 Missing** (grade A). Since
> then the multi-tenant platform expansion landed on `main` (35+ PRs), which changes several
> section-4/5 judgements: **MFA shipped** (TOTP enrollment/challenge/step-up + tenant policy,
> mt6_10 — the ADR-0013 deferral and the "revisit before launch" consequence are resolved);
> **payment provider architecture** replaced the single-adapter posture with a keyed registry +
> fail-closed LocalMock/Sandbox/Production modes and boot guards (ADR-0039); **tenant isolation
> expanded to PostgreSQL RLS** across services (`FORCE ROW LEVEL SECURITY`, transaction-scoped
> tenant scopes, ADR-0024); **product visibility is status-gated** (Inactive products hidden from
> public search/detail) with per-currency price gating and storefront-configured tax (ADR-0038).
> Gap 5 (saga doc-comment) and gap 6 (unrun browser flows) were addressed by later hardening +
> the Playwright E2E suites in CI. Remaining launch gates are unchanged and non-code: business
> registration → live Stripe/Xero + carrier credentials, external pen test, managed cluster.
> The matrix below is retained as the point-in-time 2026-06-15 record.

> **Update 2026-06-15 — FR-7 closed (BL-1).** Post-purchase guest→account conversion is now implemented: Identity publishes `EmailVerified` on verification; Ordering's `GuestOrderAttachConsumer` attaches prior guest orders by verified email; a `/convert-guest` endpoint and a storefront convert form complete the UX. Verified by 2 integration tests, a live cross-service run, and a storefront E2E assertion. Revised tally: **16 Met / 4 Partial / 0 Missing** (grade A− → A). The remaining Partials (FR-12 admin catalog CRUD, NFR-2/5/7 measurement) are backlog BL-2/6/7.
**Reviewer:** Conformance Reviewer (evidence-based, code-verified)
**Date:** 2026-06-15
**Branch:** `main` (committed)
**Scope:** Entire PRD (`docs/prd/`), 20 ADRs, plan-status file, and the actual code under `src/`, `tests/`, `scripts/`.

> Method: every claim below was confirmed by opening files / grepping the repo, not by trusting the
> PRD's own ✅ marks or the plan-status file. Paths are absolute-from-repo-root. Where the PRD or status
> file overstates reality, it is flagged explicitly.

---

## 1. FR / NFR Conformance Matrix

Legend: ✅ Met · ⚠️ Partial · ❌ Missing · ➖ Deferred-by-design

### Functional Requirements (from `docs/prd/3commerce/11-success-criteria.md`)

| ID | Requirement (abridged) | Status | Evidence |
|----|------------------------|--------|----------|
| FR-1 | Import ≥10k SKUs via `ISupplierImporter`; accepted/rejected counts in admin | ✅ Met | `src/Services/Catalog/Infrastructure/Importers/SampleDataImporter.cs`; counts surfaced via `AdminEndpoints.cs` `GET /admin/import-runs` and `src/Admin/Components/Pages/Imports.razor`; asserted by `tests/.../CatalogSearchTests.cs` (10.5k rows) |
| FR-2 | Typo-tolerant (`pg_trgm`) attribute-filterable search | ✅ Met | FTS+trigram migration (task_17); `ProductsEndpoints.cs` `GET /products`; tests `Typo_search_falls_back_to_trigram`, `Category_and_attribute_filters_apply`; e2e L11a–d |
| FR-3 | Anonymous cart persists (cookie) and merges into user cart on login | ✅ Met | `src/Services/Ordering/Infrastructure/CartService.cs` (anon `CartKey`+null UserId, merge into user cart on login) |
| FR-4 | Guest checkout with email + shipping only; no forced registration | ✅ Met | `src/Services/Ordering/Api/Endpoints/CheckoutEndpoints.cs` (UserId nullable); e2e L15–L16 checks out as `e2e@example.com` with no auth |
| FR-5 | Checkout = MassTransit saga, terminal state on success/declined/timeout | ✅ Met | `src/Services/Ordering/Infrastructure/Sagas/CheckoutStateMachine.cs` (Confirmed/Cancelled + 30-min `ExpiryTimeout`); test `Guest_checkout_confirms_and_posts_a_balanced_sale`; e2e L17 |
| FR-6 | Every order line carries a fulfillment source (`Unassigned` allowed) | ✅ Met | Fulfillment groups shipments by source; test `OrderConfirmed_creates_shipments_grouped_by_source`; per-line source in `OrderLine` |
| FR-7 | Guests convert to accounts post-purchase; **prior orders attach** | ❌ Missing | No conversion method on `IAuthService` (`src/Services/Identity/Domain/IAuthService.cs` has only Register/Login/Logout/Verify/Reset/Introspect). No `/convert` endpoint, no storefront "set a password" page. `ListMyOrders` filters strictly `o.UserId == uid` (`OrdersEndpoints.cs`); **no consumer back-fills UserId on guest orders by email** → prior guest orders can never attach. status file marks task_31 "FR-7" done — **overstated.** |
| FR-8 | Email verification, password reset, addresses, order history | ✅ Met | `AuthEndpoints.cs` (verify-email, password-reset/request+confirm); `ProfileEndpoints.cs` (addresses CRUD, ownership-scoped); order history `OrdersEndpoints.cs` `GET /orders`. (Order history only covers authenticated-placed orders — see FR-7.) |
| FR-9 | Order-linked tickets; RMA `Requested→…→RefundIssued` | ✅ Met | `src/Services/Support/Api/Endpoints/TicketEndpoints.cs`; `RmaStateMachine.cs`; test `Approved_rma_drives_the_refund_and_reaches_RefundIssued` |
| FR-10 | Approved RMA runs refund saga (ledger reversal + provider + Xero); double-approve idempotent | ✅ Met | `RmaStateMachine.cs` → single `RefundRequested` → `ExecuteRefundConsumer.cs` (idempotent on RefundId); test `Double_approve_is_a_no_op` |
| FR-11 | Nightly Xero job posts one balanced summary journal/day | ✅ Met | `src/Services/Payments/Infrastructure/Xero/DailyJournalJob.cs` + pure `XeroJournalBuilder.BuildDaily` (nets to zero because ledger is balanced); unit-tested `XeroJournalBuilderTests.cs`. Posts via `LoggingXeroClient` (no real org — see Deviations). |
| FR-12 | Admin: catalog **CRUD**, order inspection, import monitoring, RMA queue, refund issuing — admin-gated | ⚠️ Partial | Order/RMA/Ledger/Xero/Imports pages exist (`src/Admin/Components/Pages/`). **Catalog CRUD is largely absent:** API exposes only `DELETE /admin/products/{id}` (`Catalog/.../AdminEndpoints.cs`) — no create/update product endpoint — and there is **no Products/Catalog page in the Blazor admin** (no `Products.razor`/`Catalog.razor`). Admin gating present (cookie auth + admin-role + IP allowlist). |
| FR-13 | Transactional emails on verify, reset, order confirmation, tracking, RMA changes | ✅ Met | Consumers in `src/Workers/Notifications/Consumers/`: UserRegistered, PasswordResetRequested, OrderConfirmed, TrackingAssigned, RmaStateChanged, TicketOpened; templates in `Email/EmailTemplates.cs`. Delivered via `LoggingEmailSender` (sandbox) — see Deviations. |

### Non-Functional Requirements

| ID | Requirement (abridged) | Status | Evidence |
|----|------------------------|--------|----------|
| NFR-1 | Every ledger txn balances — **DB constraint** + tests | ✅ Met | DB `CONSTRAINT TRIGGER trg_ledger_balance` (DEFERRABLE INITIALLY DEFERRED) + line check-constraints `ck_line_nonneg`, `ck_line_one_side` in `Payments/.../Migrations/20260613080707_PaymentsLedger.cs`. Tests `Balanced_entry_commits_and_trial_balance_is_zero`, `Unbalanced_entry_is_rejected_at_commit`; e2e L18/L19 assert trial balance = 0. **Verified real, not just app-level.** |
| NFR-2 | Kill any service mid-checkout, restart → correct terminal state, **automated chaos test** | ⚠️ Partial | Durable mechanisms exist: durable RabbitMQ queues + EF outbox + saga `ExpiryTimeout`. But the only resilience test is `Outbox_message_survives_consumer_downtime` in `tests/.../SpineTests.cs` — on the **ping-pong spine, not the checkout saga**. No test kills a service mid-checkout. The specific chaos test NFR-2 promises is not present for checkout. |
| NFR-3 | Replaying any RabbitMQ msg / Stripe webhook → no dup side effects | ✅ Met | EF inbox dedup (test `Duplicate_delivery_produces_single_pong`); webhook dedup `WebhookInbox` + `Duplicate_payment_webhook_posts_one_journal_entry`; refund idempotent on RefundId (`ExecuteRefundConsumer.cs`); refund endpoint `IdempotencyRecord` (request-hash). |
| NFR-4 | No service reads another's DB; cross-service data via events only | ✅ Met | Per-service DbContexts; Ordering keeps its own `ProductCopies` projection via `ProductCopyConsumer.cs`; Fulfillment fed by enriched `OrderConfirmed` (lines published by Order owner, not saga). No cross-DB connection strings found. |
| NFR-5 | Search p95 <500ms; product SSR p95 <800ms @10k SKUs | ⚠️ Partial | Search p95 verified: integration `CatalogSearchTests` (NFR-5) + e2e L12 asserts p95 < 0.5s (status reports ~8.5ms). **Product-page SSR p95 <800ms is not measured** by any automated check (L14 only asserts the page renders). |
| NFR-6 | Cookie flags, Argon2id params, gateway header-strip, rate limiting — tested; ASVS L1 self-audit | ✅ Met | Cookie `HttpOnly/Secure(IsHttps)/SameSite=Lax` (`Identity/.../AuthEndpoints.cs`); Argon2id m=19456,t=2,p=1 via Konscious (`Argon2idPasswordHasher.cs` + tests); header strip `SessionAuthMiddleware.cs` (`Headers.Remove(ClaimsHeader)`); per-IP rate limiting incl. tighter auth-path limits (`Gateway/Program.cs`); no-enumeration tests; ASVS L1 self-audit task_43. (Note: cookie `Secure` is `Request.IsHttps`-conditional — correct for local HTTP, relies on edge TLS in deploy.) |
| NFR-7 | One checkout = one OpenTelemetry trace gateway→Ordering→Payments→Notifications | ⚠️ Partial | OTel wired across services + gateway (`Gateway/Program.cs`, BuildingBlocks). Spine test comment notes one TraceId spans services. But **no automated test asserts a single trace spans the full checkout path**; only console/OTLP exporters configured. Plausibly works; unverified by test. |
| NFR-8 | Logout / password-reset invalidates sessions ≤60s everywhere | ✅ Met | Gateway caches positive introspection ≤60s (`SessionAuthMiddleware.CacheTtl = 60s`); reset revokes ALL sessions (`ConfirmPasswordResetAsync`); tests `Session_is_revoked_immediately_on_logout`, `Password_reset_changes_password_and_revokes_sessions`; e2e L13. |

**Tally:** FR — 10 Met / 1 Partial (FR-12) / 1 Missing (FR-7) / 0 Deferred (FR-13 ✅ via logging sender).
NFR — 5 Met / 3 Partial (NFR-2, NFR-5, NFR-7) / 0 Missing / 0 Deferred.
**Combined: 15 Met · 4 Partial · 1 Missing · 0 Deferred-by-design (of 21).**

---

## 2. MVP Scope Conformance (`docs/prd/3commerce/04-mvp-scope.md`)

### In-scope ✅ items — verification

- **Catalog & Search** — all four implemented (neutral schema, `ISupplierImporter`+SampleDataImporter, Postgres FTS+pg_trgm behind `ISearchProvider`, category/attribute filter). ✅
- **Cart & Checkout** — anon cookie cart, guest checkout, checkout saga, per-line fulfillment source, single configurable currency (`STORE_CURRENCY`), DAP shipping: all present. ⚠️ **Post-purchase account conversion is listed ✅ in scope but is NOT implemented** (see FR-7).
- **Accounts** — registration/login/logout (opaque cookie), Argon2id (Konscious), email verification + reset, profile + addresses + order history, admin-role claim: all present. ✅
- **Payments & Ledger** — double-entry ledger as source of truth (DB-enforced), `IPaymentProvider` abstraction, webhook ingestion + dedup inbox, refund saga (full/partial), `ITaxStrategy` flat placeholder (`FlatRateTaxStrategy.cs`): all present. ✅ (Stripe runs against a `FakePaymentProvider` in dev/test — Deviation §3.)
- **Support & RMA** — order-linked typed-reason tickets, RMA state machine, refund saga Support→Payments→ledger→provider→Xero, both-direction emails: all present. ✅ (Guest signed-link access simplified — §3.)
- **Admin (Blazor)** — order list/detail, RMA queue, refund issuing, admin-role + subdomain + IP allowlist: present. ⚠️ **Catalog CRUD is the weak spot** (delete-only API, no product-management UI) — FR-12.
- **Technical** — 6 services, MassTransit+RabbitMQ async + EF outbox + idempotent consumers, one Postgres / DB-per-service / migrations per service / no cross-joins, event-maintained read models (`ProductCopies`), YARP single-origin gateway with session validation + signed-claims forwarding + rate limiting, Next.js SSR storefront, OpenTelemetry, xUnit+Testcontainers + ledger-invariant tests: all present. ✅
- **Integration** — Stripe test-mode (faked), Xero nightly journals (logging client), email behind `IEmailSender`: present with documented fakes. ✅ (§3)
- **Deployment** — `dotnet run` per service + compose infra; Dockerfiles ×8 (build verification). ✅

### Out-of-scope ❌ items — none crept in (confirmed)

Multi-currency, discount/promotions engine, real-time carrier rates, MFA/social/passkeys, account-deletion self-service, Polar adapter, second payment rail, Stripe Tax/OSS, live chat/KB/SLAs, admin dashboards/analytics, granular staff permissions, **Kubernetes manifests**, event sourcing/Kafka/Dapr, CQRS frameworks, real supplier feeds, dedicated search engine, reviews/ratings — **none found in the codebase.** Scope discipline is strong. The only in-scope ✅ item not delivered is post-purchase conversion (above).

---

## 3. Deviations from Plan / ADRs

All major deviations are **documented** in the plan-status file's Phase-3/Phase-4 notes and/or ADR-0015. Cross-checked:

| Deviation | Documented? | Evidence |
|-----------|-------------|----------|
| **Stripe → `FakePaymentProvider`** (no account/CLI). Real `StripePaymentProvider.cs` written but dev/test use a deterministic fake; success simulated via dev-only `POST /dev/simulate-payment/{intentId}` feeding the **same** `PaymentEventProcessor` as the real webhook (so dedup/idempotency still exercised). | Yes (Phase-3 notes; ADR-0015) | `Infrastructure/Payments/FakePaymentProvider.cs`, `WebhookEndpoints.cs` (sim gated to `IsDevelopment()`) |
| **Xero → `LoggingXeroClient`** (no org/creds). Pure `XeroJournalBuilder` unit-tested; `IXeroClient` is a future swap like Stripe. | Yes (Phase-4 notes; ADR-0017) | `Infrastructure/Xero/LoggingXeroClient.cs`, `XeroJournalBuilder.cs` |
| **Email → `LoggingEmailSender`** (sandbox; tokens read from log in e2e). | Yes (implied; Phase-2 note) | `Workers/Notifications/Email/LoggingEmailSender.cs` |
| **MassTransit pinned 8.5.10** (v9 commercial). | Yes (task_1) | `Directory.Packages.props` + comment |
| **Guest signed-link tickets → authenticated-only.** Tickets/RMA require `CustomerPolicy`. | Yes (explicit in-code note) | `Support/.../TicketEndpoints.cs` ("Guest signed-link access is a documented v1 simplification") |
| **Saga must start before payment succeeds.** `CheckoutStateMachine.Initially` only handles `CartSubmitted`; a `PaymentSucceeded` arriving first is not buffered into a terminal state. NOTE: the **saga's own doc-comment claims "Out-of-order webhooks are tolerated by the state guards"** — this contradicts the status file's honest admission that fully out-of-order tolerance is future hardening. Minor honesty mismatch. | Partly (status file honest; **code comment overstates**) | `Ordering/.../Sagas/CheckoutStateMachine.cs` vs. Phase-3 notes |
| **Dev-only ES256 keypair + admin password committed.** Private key in `src/Gateway/appsettings.json` with a "DEV-ONLY … generate fresh keys per env" note; `dev-admin-password-1` in e2e script. | Yes (in-file note) | `Gateway/appsettings.json` `InternalAuth:PrivateKey` |
| **k8s / cloud deploy deferred.** No manifests present. | Yes (MVP scope ❌; ADR-0015) | (absence confirmed) |
| **`IdempotencyKeyFilter` is per-endpoint, not generic BuildingBlocks filter.** | Yes (Phase-3 notes) | `Payments/Domain/IdempotencyRecord.cs`, refund endpoint |
| **Saga scheduler = MassTransit in-memory Quartz** (no RabbitMQ delayed-message plugin). | Yes (Phase-3 notes) | `MassTransit.Quartz` in `Directory.Packages.props` |
| **Plan-status shows task_23 & task_34 `in_progress`** despite "MVP-complete". Their downstream tasks are `done` and the code (contracts, Fulfillment shipments/tracking) is present and tested — so the status markers are stale, not a real gap. | Stale marker | `.ai-shared/plans/plan_status_executions.md` rows 32, 43 |

No **undocumented** architectural deviations were found. The fakes are honest, interface-seamed, and ADR-justified.

---

## 4. Gaps & Risks (claimed vs. actually working)

1. **FR-7 overstated (highest-severity honesty gap).** Status file marks "FR-4, FR-7 done," but post-purchase account conversion does not exist (no `IAuthService` method, no endpoint, no UI) and prior guest orders cannot attach (`UserId` never back-filled by email). A guest can only `register` fresh; their past orders stay orphaned. **This is the one MVP ✅ item that is actually missing.**
2. **FR-12 catalog CRUD missing in admin.** Only `DELETE /admin/products/{id}`; no create/edit product API or Blazor page. Import monitoring ≠ catalog management.
3. **NFR-2 chaos test is on the spine, not checkout.** The resilience *mechanisms* are real, but the "automated chaos test" the NFR promises does not exercise a mid-checkout service kill. Resilience for the money path is asserted by design, not by test.
4. **NFR-5 product-SSR p95 and NFR-7 single-trace are unverified by tests.** Both are plausibly satisfied (OTel wired; SSR renders) but no automated check measures them — they rest on assertion.
5. **Saga doc-comment contradicts known limitation.** `CheckoutStateMachine` claims out-of-order webhook tolerance it doesn't have; the honest version is in the status notes. Low real-world risk (client confirms after checkout returns) but a doc/code honesty mismatch in the most critical saga.
6. **Browser/admin flows largely unrun here.** Status notes admit Blazor and storefront flows are "build-validated, browser flows untested here"; e2e L20 Playwright is opt-in/skippable. UI correctness is less proven than backend.
7. **Committed dev private key.** Intentional and labelled, but a real key-rotation discipline must be enforced at deploy (it's a launch-gate, acceptably out of build scope).

---

## 5. Verdict

**Overall conformance grade: A− (strong, honest MVP with a few real gaps).**

The backbone the PRD cares most about — six isolated services, async saga-driven money flow, and a **genuinely DB-enforced** double-entry ledger — is implemented faithfully and well-tested. Deviations are almost entirely the deliberate "no legal entity yet" fakes (ADR-0015), cleanly seamed behind interfaces and documented. Two FRs miss the mark (FR-7 missing, FR-12 partial) and three NFRs are mechanism-present-but-test-light (NFR-2/5/7). Scope discipline against the ❌ list is excellent.

### Top 5 strengths
1. **Ledger integrity is real, not app-level** — deferred balance `CONSTRAINT TRIGGER` + append-only triggers + line check-constraints in `PaymentsLedger.cs`; trial-balance = 0 asserted in tests and live e2e.
2. **Custom auth done by the book** — Argon2id (Konscious, OWASP params), opaque cookie + ES256 internal-claims with gateway-only private key, header-strip, no-enumeration, ≤60s revocation — all test-backed.
3. **Single honest refund path** — RMA saga and admin both funnel through one `RefundRequested` → `ExecuteRefundConsumer` (idempotent, over-refund-guarded, proportional tax reversal); double-approve is a no-op.
4. **Dev fakes share the real code path** — `simulate-payment` flows through the same `PaymentEventProcessor`/dedup inbox as the Stripe webhook, so idempotency/webhook logic is genuinely exercised despite no Stripe account.
5. **Clean service isolation + scope discipline** — DB-per-service, event-maintained read models (`ProductCopies`, enriched `OrderConfirmed`), and zero out-of-scope creep.

### Top 5 gaps / limitations
1. **FR-7 post-purchase conversion is missing** — no convert method/endpoint/UI; guest orders never attach to accounts (status file overstates this as done).
2. **FR-12 catalog CRUD absent in admin** — only product *delete*; no create/edit API or Blazor product page.
3. **NFR-2 chaos test covers the ping-pong spine, not the checkout saga** — money-path resilience is by-design, not by-test.
4. **NFR-5 SSR-latency & NFR-7 end-to-end-trace are unmeasured** — asserted, not verified.
5. **`CheckoutStateMachine` doc-comment overclaims out-of-order tolerance** the code lacks — a small but notable honesty mismatch in the most critical saga; UI flows also remain mostly unrun.
