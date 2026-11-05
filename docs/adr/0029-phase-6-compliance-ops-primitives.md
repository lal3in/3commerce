# ADR-0029: Phase 6 compliance/ops primitives — capability-first, services deferred

## Status

Accepted — implemented as reusable primitives through Phase 6 (mt6_1–mt6_13). Amended 2026-11-04 (audit hash at stored precision).

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

## Amendment 2026-11-04 — the audit hash covers the time Postgres stores

`AuditEntry.Hash` is SHA-256 over the canonical content, which includes `OccurredAt.ToString("O")`: seven
fractional digits, i.e. 100 ns ticks. `timestamptz` keeps microseconds and Npgsql **truncates** the seventh
digit on write. A Linux clock fills that digit (a macOS clock does not), so a local chain read back from
Postgres reported ~9 in 10 Linux-written entries as tampered (`audit_chain_timestamp_precision`). Nothing
verified a Postgres-backed chain yet (`AuditRecorder.VerifyAsync` has no production caller; the Entity tests
use EF InMemory), so the bug was latent. The owner chose option (a): truncate new entries, and keep verifying
the rows already written.

**New entries.** `AuditChain.Append` stamps `OccurredAt` at stored precision
(`AuditEntry.StoredPrecision`, whole microseconds) **before** hashing. The entry in memory, the published
`AuditEntryRecorded` / stream fact, the central projection's row and the local row reloaded from Postgres all
hold, and hash, the same value. This applies to the local chain (Entity) and to the publish-only
`AuditEmitter` (Payments, Identity, Catalog, Support, Ordering), which hashes through the same `Append`.

**Rows written before the fix.** Their hash covers the 100 ns original, which the database no longer holds.
Re-hashing the stored value at microsecond precision is not a fallback for them: the stored value already is
microsecond precision, and that is the exact check that fails. A link-only check (`PrevHash` → `Hash`) would
still detect deletions and insertions, but it would leave every legacy row's *content* unverified. Neither is
needed. Npgsql truncates and never rounds, so the original is the stored time plus 0–9 ticks. When the exact
hash fails and the stored time is whole microseconds, `AuditChain.Verify` tries the nine originals
stored + 1..9 ticks. Each legacy row is therefore **fully re-verified**: its content, its link and its
sequence. Verification reports how many rows needed this (`AuditVerification.LegacyPrecisionEntries`).

- **No acceptance slack.** The search never leaves the stored microsecond. A stored time moved by any amount
  lands in another microsecond, whose nine candidates cannot include the original. So the set of stored rows
  that verify is the same as with an exact check on the true original. A time with a sub-microsecond part
  (not something Postgres returns) is checked exactly only. The cost is at most nine extra SHA-256 hashes,
  and only for a row that fails the exact check.
- **No schema change.** A `HashVersion` column or a cut-over timestamp was considered. With full
  re-verification it would add no integrity, and it would need a migration in every adopting service. Rows
  that predate the fix are still identifiable from `LegacyPrecisionEntries`. A legacy row whose 100 ns digit
  happened to be 0 (every macOS-written row) verifies exactly, as it always did.
- **The truncation assumption is pinned.** `AuditChainPostgresTests` (integration, e2e-verify A4i) writes
  pre-fix rows to Postgres, asserts that `…249` comes back as `…240`, and verifies the reloaded chain. If a
  driver ever rounded, that test fails. The window would then have to be widened deliberately, and widening it
  to cover rounding would admit a one-microsecond shift. Unit coverage: `AuditStoredPrecisionTests`
  (e2e-verify A3x). The clock is pinned at `….1755249` so the case reproduces on macOS.

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
