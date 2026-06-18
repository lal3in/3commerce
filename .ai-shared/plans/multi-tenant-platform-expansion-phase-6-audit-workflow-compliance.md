# Feature: Multi-Tenant Platform Expansion — Phase 6 Audit/Workflow/Compliance

Parent plan: `.ai-shared/plans/multi-tenant-platform-expansion.md`

## Feature Description

Add the cross-cutting compliance and operations layer: local hash-chained audits, central audit projection/search, Workflow service, approval orchestration, notifications, webhooks, import/export, object storage, image variants, retention, MFA policy, region awareness, SLOs, and launch gates.

## User Story

As a platform operator, tenant owner, auditor, or finance/admin user
I want auditable workflows, approvals, exports, notifications, webhooks, retention, and operational controls
So that the platform can operate securely and meet financial-grade audit expectations.

## Problem Statement

Multi-tenant commerce with CLI, service accounts, supplier bank data, live integrations, and field-level permissions requires stronger auditability and operational workflows than ordinary logs. Scheduled jobs, approvals, exports, webhooks, and retention policies must be first-class and permissioned.

## Solution Statement

Create Audit and Workflow services, add local authoritative audit tables to every service, hash-chain audit records, implement typed Quartz/MassTransit schedules, split approval ownership across Identity/Workflow/owning services, add notifications/webhooks/import-export/object storage, and define compliance/launch gates.

## Feature Metadata

**Feature Type**: New Capability / Compliance Hardening  
**Estimated Complexity**: Very High  
**Primary Systems Affected**: new Audit service, new Workflow service, all services, Admin, CLI, Notifications worker, Gateway, object storage, docs/scripts  
**Dependencies**: Phase 1 PDP/RLS; later phase services integrate local audit and workflow hooks as they are built.

---

## CONTEXT REFERENCES

### Relevant Codebase Files IMPORTANT: YOU MUST READ THESE FILES BEFORE IMPLEMENTING!

- `AGENTS.md` - outbox, audit, docs/api, e2e-verify rules.
- `Directory.Packages.props` - `MassTransit.Quartz` package is available.
- Existing service DbContexts - add local audit tables with same named-schema/outbox pattern.
- `src/Workers/Notifications/*` - current notification worker pattern.
- `scripts/e2e-verify.sh` - must update when tests/live flows are added.
- `.ai-shared/plans/plan_status_executions.md` - status tracking format.

### New Files to Create

- `src/Services/Audit/*`
- `src/Services/Workflow/*`
- `src/BuildingBlocks/Infrastructure/Auditing/*`
- `src/BuildingBlocks/Infrastructure/Storage/*`
- `src/BuildingBlocks/Contracts/Audit/*`, `Workflow/*`, `Notifications/*`, `Webhooks/*`
- Admin pages for Audit, Workflow, Approvals, Exports, Webhooks.

### Relevant Documentation YOU SHOULD READ THESE BEFORE IMPLEMENTING!

- MassTransit Scheduling: https://masstransit.io/documentation/configuration/scheduling
- MassTransit Outbox: https://masstransit.io/documentation/configuration/middleware/outbox
- OWASP Logging Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html
- OWASP File Upload Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html
- ASP.NET Core Data Protection: https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/introduction

### Patterns to Follow

- Domain change + local audit + outbox events in same transaction.
- Central audit projection is query/reporting; local service audit is authoritative.
- Workflow orchestrates time/tasks; owning service applies domain change.
- Notifications worker can deliver email/webhook initially; future dedicated dispatcher possible.

---

## IMPLEMENTATION PLAN

### Phase 6.1: Audit framework and service

**Tasks:** local audit tables, hash chaining, central projection/search/export, sensitive-read/denied-attempt events.

### Phase 6.2: Workflow and approvals

**Tasks:** Workflow service, Quartz schedules, job runs, approval task orchestration, expiry.

### Phase 6.3: Notifications and webhooks

**Tasks:** channel abstraction, alerts, outbound tenant webhooks, inbound provider routing conventions.

### Phase 6.4: Imports/exports/storage/assets

**Tasks:** pluggable CSV/JSON adapters, async exports, object storage, image variants.

### Phase 6.5: Compliance/security/ops hardening

**Tasks:** retention, MFA toggle/policy, region-awareness, SLO monitoring, launch gates, docs/status/e2e.

---

## STEP-BY-STEP TASKS

### mt6_1 CREATE Audit service and local audit framework

- **IMPLEMENT**: Audit service, central projection/search, local per-service audit tables, append-only model, hash chaining, source hash preservation.
- **PATTERN**: service skeleton + outbox from existing services.
- **GOTCHA**: Central audit is not source of truth; local service audit is authoritative.
- **VALIDATE**: `dotnet test tests/ --filter Audit`

### mt6_2 ADD audit coverage rules

- **IMPLEMENT**: audit mutations, sensitive reads/exports, high-risk denied attempts, field reveal reason, MasterGlobal high-risk reason, audit retention.
- **PATTERN**: PDP decision metadata from Phase 1.
- **GOTCHA**: Do not audit every ordinary read; avoid PII/secrets in audit payloads.
- **VALIDATE**: `dotnet test tests/ --filter SensitiveAudit`

### mt6_3 CREATE Workflow service

- **IMPLEMENT**: service projects, Quartz.NET + MassTransit typed schedules, job definitions/runs, retries, dashboards/API/CLI.
- **PATTERN**: MassTransit service setup; `MassTransit.Quartz` central package.
- **GOTCHA**: No arbitrary admin-defined SQL/procedure/shell execution.
- **VALIDATE**: `dotnet test src/Services/Workflow/tests/3commerce.Workflow.Tests.csproj`

### mt6_4 ADD approval orchestration

- **IMPLEMENT**: Identity/Authz approval policy, Workflow approval tasks, owning service pending change/application, expiry by risk/action.
- **PATTERN**: PDP approval metadata and local audit/outbox transactions.
- **GOTCHA**: Requester cannot approve; service accounts cannot approve; MasterGlobal bypasses approval but needs reason/audit.
- **VALIDATE**: `dotnet test tests/ --filter ApprovalWorkflow`

### mt6_5 UPDATE Notifications with channel abstraction and alerts

- **IMPLEMENT**: email-first channel abstraction, high-risk alerts, notification preferences/policies, delivery attempts.
- **PATTERN**: existing Notifications worker.
- **GOTCHA**: Sensitive data minimized in notification content.
- **VALIDATE**: `dotnet test 3commerce.sln --filter Notifications`

### mt6_6 ADD outbound tenant webhooks

- **IMPLEMENT**: tenant subscriptions, signed deliveries, retry/backoff, delivery logs, endpoint validation, Admin/CLI views.
- **PATTERN**: notification channel worker initially.
- **GOTCHA**: Do not webhook high-volume behavior analytics by default.
- **VALIDATE**: `dotnet test tests/ --filter WebhookDelivery`

### mt6_7 ADD inbound provider webhook routing conventions

- **IMPLEMENT**: Gateway routes provider webhooks to owning services; owning service verifies signature and idempotency.
- **PATTERN**: existing Stripe webhook in Payments.
- **GOTCHA**: Gateway does basic protection/routing only; verification remains in owning service.
- **VALIDATE**: `dotnet test tests/ --filter ProviderWebhook`

### mt6_8 ADD import/export adapters and async export jobs

- **IMPLEMENT**: pluggable CSV/JSON first, async large/sensitive exports through Workflow, expiring signed downloads, customer data export/deletion requests.
- **PATTERN**: existing supplier importer and planned object storage.
- **GOTCHA**: Orders/ledger/audit retained where legally required; deletion means redact/anonymize where allowed.
- **VALIDATE**: `dotnet test tests/ --filter Export`

### mt6_9 ADD object storage abstraction and image variants

- **IMPLEMENT**: local/dev adapter + production object-store seam, metadata in owning services, signed URLs, upload validation, image variants.
- **PATTERN**: no shared domain ownership; storage owns bytes/access only.
- **GOTCHA**: Strip dangerous metadata; sensitive file access audited.
- **VALIDATE**: `dotnet test 3commerce.sln --filter Storage`

### mt6_10 ADD MFA/step-up toggle and tenant policy

- **IMPLEMENT**: MFA feature toggle, platform minimums, tenant-configurable stronger policy, step-up hooks for high-risk actions.
- **PATTERN**: Identity/Authz policy catalog.
- **GOTCHA**: MFA can be disabled initially but target architecture must support it.
- **VALIDATE**: `dotnet test src/Services/Identity/tests/3commerce.Identity.Tests.csproj --filter MfaPolicy`

### mt6_11 ADD region-aware operations and retention

- **IMPLEMENT**: tenant home region metadata, minimal global domain registry, region-aware backups/retention metadata, no tenant region moves initially.
- **PATTERN**: tenant/domain registry from Phase 1.
- **GOTCHA**: One physical region initially; do not build cross-region migration feature.
- **VALIDATE**: `dotnet test tests/ --filter Region`

### mt6_12 ADD docs, e2e-verify, SLOs, launch gates

- **IMPLEMENT**: API contracts, ADR index, AGENTS structure, e2e-verify tasks, SLO monitoring notes, launch gates for pen test/load test/live integrations.
- **PATTERN**: repo Rules in `AGENTS.md`.
- **GOTCHA**: Update regression list whenever tests/live journeys change.
- **VALIDATE**: `scripts/e2e-verify.sh`

---

## TESTING STRATEGY

- Unit: audit hash chain, approval expiry, workflow schedules, webhook signing, retention policy, storage validation.
- Integration: domain mutation + audit + outbox transaction, central projection, approval apply, export job, webhook retries.
- E2E: admin approval queue, sensitive export, audit search, workflow scheduled publish/feed generation, webhook delivery log.

## VALIDATION COMMANDS

```bash
dotnet build 3commerce.sln
dotnet format --verify-no-changes
dotnet test 3commerce.sln
dotnet test tests/ --filter Category=Integration
scripts/e2e-verify.sh
```

## ACCEPTANCE CRITERIA

- [ ] Local authoritative audit logs exist and are hash-chained.
- [ ] Central Audit service projects/searches audit events.
- [ ] Workflow service schedules typed jobs and orchestrates approvals.
- [ ] Notifications/webhooks are channelized, signed/retried where applicable.
- [ ] Imports/exports are permissioned and sensitive/large exports async/audited.
- [ ] Object storage abstraction and image variants implemented.
- [ ] Retention, MFA policy, region metadata, SLOs, and launch gates documented/enforced where in scope.

## NOTES

This phase cross-cuts every service. For earlier phases, implement the minimum audit/workflow hooks needed locally, then consolidate under this phase when the Audit/Workflow services land.

---

## Addendum — admin mission-control grill (2026-06-18)

Adds the operator "mission control" surface and the observability stack from the design grill. Verified current state: OTel is wired for **traces only** (no metrics backend; no Prometheus/Grafana deployed); RabbitMQ runs the **management plugin** (`rabbitmq:4-management`, in-cluster, not published to host); each service exposes a **/health** endpoint.

### mt6_13 ADD observability metrics stack (OTel metrics + Prometheus + Grafana)

- **IMPLEMENT**: Extend `AddServiceTelemetry` to export **metrics** (OTLP), deploy **Prometheus + Grafana** (compose + Helm), and provision dashboards (per-service latency/throughput/error rate, queue depth, DB, resource). Add an OTel Collector if needed. Prod creds launch-gated.
- **PATTERN**: `BuildingBlocks/Infrastructure/Observability/OtelExtensions.cs`; container/Helm wiring (ADR-0021).
- **GOTCHA**: Metrics/dashboards are ops data, never financial truth; secure Grafana behind admin auth/network.
- **VALIDATE**: `dotnet build 3commerce.sln` plus compose/helm smoke for the new services.

### mt6_14 ADD admin mission-control console

- **IMPLEMENT**: An operator console in Admin with: **Message Bus** (live RabbitMQ management-API stats — queue depths/rates/consumers/DLQ — plus a searchable event timeline from a MassTransit **wiretap** observer feeding the Audit store); **Service/Infra Health** (per-service /health heartbeats with last-seen/latency + container/pod status, plus embedded Grafana metrics from mt6_13); **Accounts** section (customer directory + unified people directory + ledger/financial-accounts views); and the **dynamic-RBAC management** surface (Phase 1 mt1_8). All views permission-gated and audited.
- **PATTERN**: Admin GatewayClient; Audit projection/search mt6_1; RabbitMQ management API.
- **GOTCHA**: The wiretap is never order/financial truth; mask secrets/PII in message payloads. Reading the 6 service DBs directly is prohibited — go via owning-service APIs / the Audit projection.
- **VALIDATE**: `dotnet build src/Admin/3commerce.Admin.csproj`

Acceptance additions:
- [ ] OTel metrics + Prometheus + Grafana deployed (compose + Helm) with admin-embedded dashboards.
- [ ] Admin mission-control shows live broker stats + a wiretap event timeline, service/infra heartbeats, and an Accounts section.
- [ ] Mission-control views are permission-gated and audited.