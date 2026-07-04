# ADR-0029: Phase 6 compliance/ops primitives — capability-first, services deferred

## Status

Accepted — implemented as reusable primitives through Phase 6 (mt6_1–mt6_13).

## Context

Phase 6 (audit, workflow, compliance, observability) calls for several new platform services
(Audit, Workflow) and a number of cross-cutting capabilities (approval, webhooks, storage, export,
notifications, MFA, retention). While CI was quota-blocked, scaffolding new DB-owning services could
not be validated end-to-end (compose/Helm/CI/migrator), whereas domain + service + Testcontainers
integration are fully verifiable locally. We also observed that most of these capabilities are
genuinely **cross-service plumbing**, not a single service's domain.

## Decision

Deliver each Phase 6 capability **capability-first** as a small, reusable, unit-tested primitive in
`BuildingBlocks.Infrastructure.*` (or the natural policy owner), wired into one concrete service to
prove it. Defer the standalone services and persistence until CI returns; the seams make extraction a
project move, not a redesign. Where each capability lives today:

| Capability | Primitive (namespace) | First adoption | Filter |
|---|---|---|---|
| Audit (hash-chained, append-only) | `…Audit` — AuditEntry/AuditChain/AuditRecorder/EfAuditStore | Entity maker-checker | `Audit` |
| Audit coverage rules | `…Audit.AuditCategories` (mutation/denied/sensitive-read/reveal) | Entity | `SensitiveAudit` |
| Scheduled jobs | `…Scheduling` — IScheduledJob/JobExecutor/Quartz `AddScheduledJobs` | Payments daily journal | (JobExecutor) |
| Approval orchestration | `…Approval` — ApprovalTask/ApprovalRules (maker-checker, service-acct, MasterGlobal, expiry) | (generalizes Entity) | `ApprovalWorkflow` |
| Notifications channel + alerts | `…Notifications` — channel seam, policy, SecurityAlert | (worker adoption pending) | `Notifications` |
| Outbound webhooks | `…Webhooks` — signature/endpoint/delivery/dispatcher | (host pending) | `WebhookDelivery` |
| Inbound webhooks | `…Webhooks.InboundWebhookVerifier` + gateway route convention | stripe→payments | `ProviderWebhook` |
| Import/export | `…Export` — CsvExport/SignedDownload/Redaction | (job pending) | `Export` |
| Object storage | `…Storage` — IObjectStore/LocalFileObjectStore/UploadPolicy/ImageVariant | (catalog images pending) | `Storage` |
| MFA / step-up | `Identity.Domain.MfaPolicy/StepUp/Totp` | Identity login challenge + `/mfa/*` (def_1, 2026-07-04) | `MfaPolicy` |
| Region / retention | `…Governance` — TenantRegion/RetentionPolicy | (sweep pending) | `Region` |
| Observability metrics | `…Observability.AddServiceTelemetry` + deploy/observability stack | all services | (build) |

## SLO notes (mt6_12)

The mt6_13 RED metrics back these service-level objectives (ops data, not a contract):

- **Availability** ≥ 99.5% monthly per service (`/health/ready`).
- **Latency** p95 < 500 ms for catalog search + product detail (NFR-5); p95 < 800 ms other reads.
- **Errors** 5xx rate < 1% per service over 5 min (alert via mt6_5 SecurityAlert/ops channel).

Dashboards: `deploy/observability` Services-RED. Alerting wiring is launch-gated.

## Launch gates (carried)

Pen test, load test, and live provider integrations (Stripe/Xero live keys, real carrier accounts)
remain launch-gated (PRD Appendix B / `docs/help/deployment.md`). The deferred Phase 6 services
(central Audit projection, standalone Workflow) and the unpushed-branch CI restoration are tracked in
the plan status.

## Consequences

- New compliance capabilities arrive testable on day one without unvalidatable infrastructure.
- Adoption is incremental: a service opts in by registering the store/recorder and calling the
  primitive at its mutation/decision points (Entity + Payments already do).
- The "right" services (Audit projection, Workflow) still land later; the primitives become their
  internals, and the event seams (audit entries, `SubscriptionRequested`, webhook contracts) already
  exist.
