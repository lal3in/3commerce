# Feature: Phase 4 — Operations: Fulfillment, Support/RMA, Blazor admin, Xero sync, security audit

> **PRE-EXECUTION NOTE:** written before Phases 1–3 executed. Verify prior-phase artifacts (refund contract `RefundRequested`, internal-claims auth, OrderConfirmed event, ledger API) and adjust names/paths to reality before implementing.

The following plan should be complete, but its important that you validate documentation and codebase patterns and task sanity before you start implementing.

Pay special attention to naming of existing utils types and models. Import from the right files etc.

## Feature Description

Make the store runnable, not just buyable: Fulfillment (shipments per fulfillment source, tracking → customer email), Support (order-linked tickets + RMA state machine triggering the Phase 3 refund path), the Blazor Server admin app (catalog, orders, import monitoring, RMA queue, refunds), Xero accounting sync (nightly summary journals + per-refund postings), and the pre-launch security self-audit. Completing this phase completes the MVP success scenario.

## User Story

As the store operator
I want to see orders, assign tracking, answer tickets, approve refunds in one UI — with the books syncing to Xero automatically
So that I can run the business end-to-end without touching a database console or doing manual bookkeeping.

## Problem Statement

After Phase 3, money moves but operations are headless: no shipments, no customer support channel, no admin UI, no accounting. The RMA refund saga — the integration capstone touching every service — doesn't exist yet.

## Solution Statement

Fulfillment and Support consume existing events and contracts; the RMA state machine publishes the Phase 3 `RefundRequested` contract (single refund path, ADR-0014); Blazor admin drives everything through the gateway with the admin role; Xero is a write-only downstream fed nightly from the ledger (ADR-0017).

## Feature Metadata

**Feature Type**: New Capability
**Estimated Complexity**: High (breadth, not depth — four workstreams)
**Primary Systems Affected**: Fulfillment, Support, Payments (Xero), Admin (new), Notifications, Storefront (ticket/RMA UI)
**Dependencies**: Xero API (OAuth2; `Xero.NetStandard.OAuth2` SDK or minimal client), Blazor Server (.NET 10), Xero Demo Company org

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

- `docs/adr/0017-xero-nightly-summary-journals.md` — Why: sync granularity, rate limits, write-only rule.
- `docs/adr/0018-support-tickets-rma-state-machine.md` — Why: RMA states + refund saga trigger; scope exclusions (no chat/KB/SLA).
- `docs/adr/0019-blazor-server-admin.md` — Why: admin auth posture (role + subdomain + IP allowlist), CRUD-grade UI bar, no privileged DB access.
- `docs/adr/0003-per-line-item-fulfillment-source.md` — Why: shipment grouping rule.
- `docs/prd/3commerce/10-api-specification.md` (Fulfillment/Support sections) — Why: endpoint contracts incl. signed-link guest ticket access.
- `docs/prd/3commerce/09-security-configuration.md` — Why: ASVS L1 self-audit checklist scope (Task 10).
- Phase 3 `RefundRequested`/`RefundCompleted` contracts + ExecuteRefundConsumer — Why: RMA approval publishes these; do NOT create a second refund path.
- `docs/reference/components.md` §6 — Why: Blazor admin rules (disable saga buttons on click, render returned state).

### New Files to Create

- Contracts: `Fulfillment/{ShipmentCreated,TrackingAssigned}.cs`, `Support/{TicketOpened,RmaStateChanged}.cs`
- Fulfillment: `Domain/{Shipment,ShipmentLine}.cs`, `Infrastructure/Consumers/OrderConfirmedConsumer.cs` (groups lines by FulfillmentSource → shipments), `Api/Endpoints/AdminShipmentsEndpoints.cs` (list, assign tracking)
- Support: `Domain/{Ticket,TicketMessage,Rma,RmaState}.cs`, `Infrastructure/Sagas/RmaStateMachine.cs` (`Requested→Approved/Denied→AwaitingReturn→ReturnReceived→RefundIssued`; Approved w/o return path publishes `RefundRequested` directly), `Api/Endpoints/{Tickets,AdminRmas}Endpoints.cs`
- Payments (Xero): `Infrastructure/Xero/{XeroClient,XeroAuth(OAuth2+token store),DailyJournalJob,RefundPostingConsumer,SyncRun}.cs`, `Api/Endpoints/AdminXeroEndpoints.cs`
- Admin (new app): `src/Admin/3commerce.Admin.csproj` Blazor Server — `Pages/{Catalog,ImportRuns,Orders,OrderDetail,RmaQueue,Ledger,XeroSync}.razor`, `Services/GatewayApiClient.cs` (typed client, forwards admin cookie), auth via Identity cookie + admin role policy
- Storefront: `app/(account)/orders/[id]/support/page.tsx` (open ticket, typed reasons, RMA request), ticket thread page
- Notifications: ticket/RMA/tracking email templates + consumers
- Tests: `tests/3commerce.IntegrationTests/{RmaSagaTests,XeroSyncTests(mocked),FulfillmentFlowTests}.cs`

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

- [Xero API — Manual Journals](https://developer.xero.com/documentation/api/accounting/manualjournals) — Why: the nightly summary posting target.
- [Xero API — OAuth 2.0 flow + scopes](https://developer.xero.com/documentation/guides/oauth2/auth-flow/) — Why: offline_access refresh tokens; `accounting.transactions` scope; token storage.
- [Xero API — rate limits](https://developer.xero.com/documentation/guides/oauth2/limits/) — Why: 60/min, 5k/day budget (ADR-0017).
- [Blazor Server authentication/authorization](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/) — Why: cookie auth + role policy in interactive server components.
- [MassTransit saga state machine](https://masstransit.io/documentation/patterns/saga/state-machine) — Why: RMA machine mirrors Phase 3's checkout machine.

### Patterns to Follow

- RMA approval → publish `RefundRequested` (Phase 3 contract) — Support never talks to Stripe or the ledger directly.
- Admin app calls the **gateway** like any client (ADR-0019) — no service references, no DB access; admin endpoints already role-gated.
- Xero job: read yesterday's ledger entries grouped by account → one balanced ManualJournal; persist SyncRun (date, journal id, status); retry w/ backoff; **never** read Xero back as truth (R-3).
- Signed-link guest access for tickets mirrors Phase 3's signed order link.

---

## IMPLEMENTATION PLAN

### Phase 1: Foundation
Contracts; Fulfillment shipments from `OrderConfirmed`; Support ticket domain + endpoints.

### Phase 2: Core Implementation
RMA state machine wired to refund path; Xero OAuth + daily journal job + per-refund postings.

### Phase 3: Integration
Blazor admin app (all screens); storefront support UI; notification emails; gateway routes + admin subdomain/IP posture.

### Phase 4: Testing & Validation
RMA end-to-end (the MVP capstone scenario), Xero demo-org verification, ASVS L1 self-audit, full MVP success-criteria run.

---

## STEP-BY-STEP TASKS

### 1. CREATE Fulfillment shipments + tracking flow
- **IMPLEMENT**: `OrderConfirmedConsumer` groups order lines by `FulfillmentSource` → Shipment rows; `POST /admin/shipments/{id}/tracking` → `TrackingAssigned` event → Notifications email + Ordering projection updates order status.
- **VALIDATE**: integration: confirm order → shipments exist grouped correctly; assign tracking → email in sandbox + order page shows tracking

### 2. CREATE Support tickets (customer + guest signed-link)
- **IMPLEMENT**: per PRD §10: open ticket from order (typed reasons), thread messages, `TicketOpened` → Notifications; guest access via signed order-link token.
- **VALIDATE**: integration: guest opens ticket via signed link; reply round-trip emails fire

### 3. CREATE RmaStateMachine + admin RMA endpoints
- **IMPLEMENT**: saga `Requested→Approved/Denied→(AwaitingReturn→ReturnReceived)→RefundIssued`; approve (idempotent, Idempotency-Key) → for no-return refunds publish `RefundRequested` immediately, else on `ReturnReceived`; `RefundCompleted` (Phase 3) advances to `RefundIssued`; partial refunds per line.
- **GOTCHA**: double-approval must be a no-op (FR-10); deny after approve → 409.
- **VALIDATE**: RmaSagaTests: full matrix incl. double-approve, deny-path, partial refund

### 4. CREATE Xero OAuth + nightly DailyJournalJob + RefundPostingConsumer
- **IMPLEMENT**: OAuth2 connect flow (admin-triggered, demo org), encrypted token store in payments_db, refresh handling; nightly job (hosted service + cron config): yesterday's ledger grouped by account → balanced ManualJournal (sales/refunds/fees/tax lines); `RefundCompleted` → individual posting; SyncRun records; backoff retries; rate-limit guard.
- **GOTCHA**: Xero amounts are decimal-with-2dp — convert from minor units at the boundary only; journal must net to zero or Xero rejects.
- **VALIDATE**: XeroSyncTests against mocked client (journal shape, idempotent rerun per date); manual: job posts to Demo Company, journal visible in Xero UI matching ledger totals

### 5. CREATE Blazor Server Admin app
- **IMPLEMENT**: project at `src/Admin` (port 5200); cookie auth against Identity + `RequireRole("admin")` globally; `GatewayApiClient` typed client; pages: Catalog CRUD, ImportRuns dashboard, Orders list/detail (payment state, per-line fulfillment, tickets), RMA queue (approve/deny with disable-on-click), Ledger viewer, Xero sync status. CRUD-grade UI (QuickGrid), no design ambitions (ADR-0019).
- **GOTCHA**: saga-triggering buttons render returned state and disable during flight (components.md §6); admin must work end-to-end through the gateway only.
- **VALIDATE**: `dotnet run --project src/Admin` → login as admin → approve an RMA → refund completes; non-admin login → 403 everywhere

### 6. CREATE storefront support/RMA UI
- **IMPLEMENT**: "Report a problem" on order page → typed reason form → ticket thread view → RMA request with per-line selection; state shown pending-first.
- **VALIDATE**: manual: <2 min order-page→RMA-confirmation flow (PRD UX goal); lint/typecheck clean

### 7. ADD admin network posture
- **IMPLEMENT**: admin on separate subdomain/port with IP-allowlist middleware (config-driven CIDRs); document local + future-k8s mapping in AGENTS.md Notes.
- **VALIDATE**: request from non-allowed IP (spoof via config toggle in test) → 403 before auth

### 8. ADD remaining Notification templates + wire all events
- **IMPLEMENT**: tracking-assigned, ticket-reply, RMA state-change templates; consistent layout.
- **VALIDATE**: each event type produces exactly one email in sandbox (idempotency re-check)

### 9. RUN full MVP success scenario (PRD TL;DR metrics)
- **IMPLEMENT**: scripted walkthrough: import 10k SKUs → search → guest purchase (test card) → emails → ship+track → open RMA → approve → refund on card + balanced ledger reversal + Xero postings. Record as `docs/runbooks/mvp-walkthrough.md`.
- **VALIDATE**: every step green; trial balance zero; Xero matches ledger

### 10. RUN OWASP ASVS L1 self-audit + dependency scan
- **IMPLEMENT**: audit Identity + gateway against ASVS L1 (checklist committed as `docs/security/asvs-l1-audit.md` with pass/fail/na per item); `dotnet list package --vulnerable` + `npm audit` gates in CI; fix or ticket findings.
- **VALIDATE**: audit doc complete; CI security steps green

### 11. UPDATE docs + PRD status
- **IMPLEMENT**: OpenAPI contracts for Fulfillment/Support → `docs/api/` + index; AGENTS.md structure (Admin app real); PRD.md status → reflects MVP-complete pending review; status file final pass.
- **VALIDATE**: `ls docs/api/` covers all six services; status file shows all Phase 4 tasks done

---

## TESTING STRATEGY

### Unit Tests
RMA state machine transitions (TestHarness, full matrix); Xero journal builder (grouping, zero-net, minor-unit→decimal conversion incl. rounding); shipment grouping by FulfillmentSource.

### Integration Tests
RMA end-to-end through real refund path (Stripe test); ticket lifecycle with guest link; Xero job idempotent re-run per date (mocked client); fulfillment event chain.

### Edge Cases
RMA on partially-refunded order; approve after customer account deleted (manual deletion path); Xero token expired mid-job (refresh + resume); journal for a zero-sales day (skip, don't post empty); tracking assigned twice (idempotent, one email).

---

## VALIDATION COMMANDS

### Level 1: Syntax & Style
```bash
dotnet build 3commerce.sln && dotnet format --verify-no-changes
cd src/Storefront && npm run lint && npx tsc --noEmit
```
### Level 2: Unit Tests
```bash
dotnet test 3commerce.sln --filter Category!=Integration
```
### Level 3: Integration Tests
```bash
dotnet test tests/3commerce.IntegrationTests --filter Category=Integration
```
### Level 4: Manual Validation
The full MVP walkthrough (Task 9) — runbook in `docs/runbooks/mvp-walkthrough.md`.

### Level 5: Additional Validation (Optional)
Xero Demo Company UI cross-check; RabbitMQ UI: zero dead-letter accumulation after walkthrough.

---

## ACCEPTANCE CRITERIA

- [ ] FR-9–FR-13 pass (PRD §11); refund saga idempotent (FR-10 double-approve no-op)
- [ ] MVP success definition met end-to-end (PRD §11 first paragraph) and recorded as runbook
- [ ] Xero demo org shows daily journal + per-refund postings matching the ledger to the cent
- [ ] Admin unreachable without admin role AND allowlisted IP
- [ ] ASVS L1 audit document complete with no open critical findings
- [ ] No chat/KB/SLA/agent-assignment code crept in (ADR-0018 scope)

## COMPLETION CHECKLIST

- [ ] All tasks completed in order; validations passed immediately
- [ ] Full suite + lint/typecheck clean (C# + TS)
- [ ] MVP walkthrough reproducible from the runbook
- [ ] All `docs/api/` contracts + indexes current; status file complete
- [ ] PRD status updated

## NOTES

- This phase is wide, not deep — workstreams 1/2 (Fulfillment+Support) and 4 (Xero) are independent and can interleave; Blazor admin (5) depends on all APIs existing.
- Xero sync must never sit in any checkout/refund critical path — async only (ADR-0017).
- Post-phase: Kubernetes learning track + company registration unblock launch (PRD §13 / Appendix B).
- Confidence: 7/10 — individual pieces are simpler than Phase 3; risk is breadth/integration drift against phase-3 reality, hence the pre-execution note.
