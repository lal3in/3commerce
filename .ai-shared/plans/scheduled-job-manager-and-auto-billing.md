# Feature: Scheduled-Job Manager (full control) + timer-driven auto-billing (subscription auto-renew + usage prepaid auto-load)

Validate documentation and codebase patterns before implementing. This spans **BuildingBlocks scheduling**,
**Workflow** (central job read model), **Admin/Mission Control** (UI + gateway), **Payments** (subscription
auto-renew), and **Usage** (prepaid auto-load). Money/auth-sensitive — preserve the ledger invariants
([[3commerce-ledger-posting-invariants]]) and the format-verify-all-projects rule
([[3commerce-format-verify-all-projects]]).

## Decisions (from planning Q&A)
- **Job manager = FULL CONTROL** (view + run-now + pause/resume + edit schedule/cron), **surfaced by extending
  Mission Control** (not a new page).
- **Subscription auto-renew = PER-TENANT daily time** (default `12:00:00` server time, per-tenant override).
- **Usage auto-load = EVENT-DRIVEN + DAILY SAFETY NET** (top up the moment usage crosses the customer's
  threshold; a daily sweep catches any missed/failed top-ups).

## User story
As an **operator** I want to see and control every scheduled job from Mission Control, and as a **billing
platform** I want subscriptions to auto-renew on each tenant's daily schedule and metered customers to
auto-load prepaid credit when they run low — so recurring revenue is collected without manual `renew` clicks.

## Feature metadata
**Type**: Enhancement (new capability). **Complexity**: High. **Primary systems**: BuildingBlocks.Scheduling,
Workflow, Admin (Blazor), Gateway, Payments, Usage. **Dependencies**: Quartz.NET (already used),
MassTransit, existing `IScheduledJob`/`JobExecutor`/`JobRun`/`JobRunRecorded` backbone.

---

## CONTEXT REFERENCES — READ BEFORE IMPLEMENTING

- `src/BuildingBlocks/Infrastructure/Scheduling/SchedulingExtensions.cs` — `AddScheduledJobs` wires Quartz
  cron triggers at startup (lines 84-92); `ConfigureJobRuns` maps the local `JobRuns` table. **Full control
  hooks here**: apply persisted schedule overrides + pause state at startup, and expose the Quartz
  `ISchedulerFactory` for runtime control.
- `src/BuildingBlocks/Infrastructure/Scheduling/JobExecutor.cs` — records `JobRun` locally AND **publishes
  `JobRunRecorded`** (lines 87-90) so Workflow shows cross-service history. `IJobRunStore` / `EfJobRunStore<T>`.
- `src/BuildingBlocks/Infrastructure/Scheduling/JobRunRecorded.cs` — the cross-service run event. **Extend**
  with the owning service name (so the manager knows where to route control commands).
- `src/BuildingBlocks/Infrastructure/Scheduling/ScheduledJob.cs` — `IScheduledJob` (Name/CronSchedule/
  ExecuteAsync) + `JobRun` aggregate.
- `src/Services/Workflow/Infrastructure/JobRunRecordedConsumer.cs` + `Domain/WorkflowRun.cs` +
  `Api/Endpoints/WorkflowEndpoints.cs` (`/admin/workflow/runs`, `WorkflowRunDto` w/ `DurationMs`) — the central
  read model + API Mission Control renders. **Extend** into a job *registry* (service, cron, next-fire,
  enabled/paused, last-run) not just run history.
- `src/Admin/Components/Pages/MissionControl.razor` — the existing "Scheduled jobs" section (run history +
  duration). **Extend** into the full manager table with per-row actions.
- Existing jobs to register (all already flow `JobRunRecorded`): `DailyJournalScheduledJob` (Payments),
  `UsagePeriodCloseScheduledJob` (Usage), `ScheduledPublishJob` (Marketing).
- `src/Services/Payments/Infrastructure/SubscriptionService.cs` — `RenewAsync` (off-session, #206), the
  renewal charge the auto-renew sweep will call. `Domain/Subscription.cs` (`CurrentPeriodEnd`, `Status`).
- `src/Services/Payments/Api/Endpoints/SubscriptionEndpoints.cs` — `MapJobRuns` (`/admin/jobs/runs`), the
  manual `renew`/`cancel` routes.
- `src/Services/Usage/Infrastructure/UsageService.cs` — `RecordAsync` (event hook for auto-load),
  `CloseDuePeriodsAsync` (the daily sweep to extend with the auto-load safety net), `BillUnbilledOverageAsync`.
  `Domain/Usage.cs` — `UsageBalance` (add prepaid + auto-load config).
- `src/Services/Usage/Infrastructure/Scheduling/UsagePeriodCloseScheduledJob.cs` — pattern for a Quartz job.
- `src/Services/Payments/Infrastructure/Consumers/UsageOverageChargeConsumer.cs` +
  `BuildingBlocks/Contracts/Payments/UsageOverageCharge.cs` — how a usage charge reaches the rail; mirror for
  `UsageAutoLoadCharge`.

### Relevant docs
- Quartz runtime control — https://www.quartz-scheduler.net/documentation/ — `IScheduler.TriggerJob`,
  `PauseJob`/`ResumeJob`, `RescheduleJob`, `GetTrigger(...).GetNextFireTimeUtc()`.

### Patterns to follow
- Segregated capabilities + mode-gating (as `IDirectDebitProvider`/`IWebhookRegistrationProvider`).
- Per-service scheduler + local `JobRuns`; central Workflow read model via `JobRunRecorded` (don't query
  services directly — ADR-0008). Mission Control reads owning-service/gateway APIs only.
- EF migration → `dotnet format` the Infrastructure csproj ([[3commerce-ef-migration-format]]); cap Npgsql pool
  in integration tests ([[3commerce-integration-test-conn-pools]]); off-session charges reuse #206 wiring.

---

## IMPLEMENTATION PLAN — phased

### Phase 1 — Scheduled-Job Manager (full control, in Mission Control)

Goal: one Mission Control surface listing every job across services with Run-now / Pause-Resume / Edit-cron.

1. **Extend `JobRunRecorded`** with `Service` (owning service key). Each `JobExecutor` publish stamps its
   service name (inject a `SchedulerServiceName` option set per host).
2. **Job registry read model (Workflow)**: new `ScheduledJobDescriptor` event `(Service, Name, Cron, Enabled,
   NextFireUtc)` published by each service at startup and after any schedule/pause change; Workflow consumes
   into a `ScheduledJob` read-model row keyed by `(Service, Name)`, merged with latest run (status/duration
   from `WorkflowRun`). New `GET /admin/workflow/jobs` returns the merged manager view.
3. **Reusable control surface (BuildingBlocks)** `MapJobControl(this IEndpointRouteBuilder)` backed by Quartz
   `ISchedulerFactory` + a `ScheduleOverrideStore`:
   - `POST /admin/jobs/{name}/run` → `scheduler.TriggerJob(key)`.
   - `POST /admin/jobs/{name}/pause` | `/resume` → `PauseJob`/`ResumeJob`.
   - `PUT /admin/jobs/{name}/schedule {cron}` → validate cron, `RescheduleJob` with a new cron trigger.
   - All mutations persist to a per-service `ScheduleOverride { JobName, Cron?, Paused }` row and re-publish the
     descriptor. `AddScheduledJobs` reads overrides at startup (override cron > code default; paused ⇒ start
     paused) so control survives restart without requiring Quartz persistent store everywhere.
   - Admin-policy authorized; each service calls `app.MapJobControl()`.
4. **Gateway**: ensure `/api/{service}/admin/jobs/*` routes forward to each service (Payments, Usage,
   Marketing, Workflow). Mission Control uses the descriptor's `Service` to pick the route.
5. **Mission Control UI** (`MissionControl.razor`): replace the read-only "Scheduled jobs" strip with a manager
   table — Service · Job · Cron · Next fire · Last run (status + duration) · State — plus per-row **Run now**,
   **Pause/Resume**, **Edit schedule** (cron input, validated). Optimistic refresh after each action.
6. **Register existing jobs**: add `MapJobControl` + descriptor publish to Payments, Usage, Marketing; the
   three existing jobs appear automatically.
7. **Tests**: control endpoints (run/pause/resume/reschedule change Quartz state + persist override);
   descriptor projection into the registry; cron validation rejects bad expressions; a paused job doesn't fire.

### Phase 2 — Subscription auto-renew on a per-tenant daily timer

8. **Per-tenant schedule**: `TenantBillingSchedule { TenantId, DailyRunTime TimeOnly = 12:00:00, Enabled = true,
   LastRunOn DateOnly? }` in Payments + admin GET/PUT endpoint. Default 12:00:00 server time.
9. **`SubscriptionAutoRenewJob`** (`IScheduledJob`, fine cadence e.g. `0 0/15 * * * ?`): for each tenant whose
   `DailyRunTime` has arrived today and `LastRunOn < today`, renew every `Active`/`Trialing` subscription with
   `CurrentPeriodEnd <= now` via off-session `RenewAsync` (#206); set `LastRunOn = today`. Idempotent — a
   second tick the same day is a no-op; a subscription already advanced is skipped. Failures dun to `PastDue`
   (existing). Records a `JobRun` and appears in the manager.
10. **Migration** `TenantBillingSchedule` (+ `dotnet format`).
11. **Tests**: a due subscription is renewed once at/after the tenant's time; not renewed before it or twice
    the same day; a tenant with `Enabled=false` is skipped; disparate per-tenant times each fire independently.

### Phase 3 — Usage prepaid auto-load (event-driven + daily safety net)

12. **Prepaid model** on `UsageBalance` (or a `PrepaidCredit` sibling): `AutoLoadEnabled`,
    `AutoLoadThresholdQuantity`, `AutoLoadReloadQuantity`, `AutoLoadPriceMinor`, `PrepaidBalanceQuantity`.
    Customer sets threshold + reload amount. Domain method `ShouldAutoLoad()` + `ApplyAutoLoad(now)`.
13. **Event-driven trigger**: in `UsageService.RecordAsync`, after applying usage, if `ShouldAutoLoad()` publish
    `UsageAutoLoadCharge (tenant, email, meter, reloadQuantity, chargeMinor, currency, reference)` off-session
    (Payments consumer mirrors `UsageOverageChargeConsumer`, charging the customer's stored instrument) and
    record a **pending** top-up; credit the prepaid balance on the succeeded webhook (reconcile) — or credit
    optimistically + compensate on failure (decide at review; default: credit on webhook success, block further
    use only if `OverageAllowed=false`).
14. **Daily safety net**: extend `CloseDuePeriodsAsync` (or a sibling in the same daily job) to find auto-load
    balances at/below threshold with no in-flight top-up and re-trigger — covering a failed/missed event charge.
15. **Config surface**: customer sets auto-load threshold + reload amount + instrument (storefront `/account`
    or admin); guard that a stored instrument exists (reuse the verified-member/instrument checks).
16. **Migration** `UsagePrepaidAutoLoad` (+ `dotnet format`).
17. **Tests**: crossing the threshold on `RecordAsync` emits exactly one `UsageAutoLoadCharge` and (on success)
    credits the balance; idempotent (no duplicate charge while one is pending); the daily safety net retries a
    failed top-up; auto-load off = classic overage behaviour unchanged.

---

## TESTING STRATEGY
Unit (domain/state machines): schedule-override precedence, cron validation, tenant-time due logic,
`ShouldAutoLoad`/`ApplyAutoLoad`. Integration (Testcontainers): control endpoints drive Quartz + persist +
re-publish descriptor; auto-renew renews due subscriptions once per tenant/day off-session; event-driven
auto-load charges + credits, daily safety net retries. Cap Npgsql pool ([[3commerce-integration-test-conn-pools]]).

## VALIDATION COMMANDS
```bash
dotnet build 3commerce.sln
dotnet format --verify-no-changes <each touched *.csproj>   # incl. every Domain project (IDE0040 gate)
dotnet test src/Services/Payments/tests/*.csproj src/Services/Usage/tests/*.csproj src/Services/Workflow/tests/*.csproj
dotnet test tests/3commerce.IntegrationTests/*.csproj --filter "FullyQualifiedName~Job|FullyQualifiedName~AutoRenew|FullyQualifiedName~AutoLoad"
```
Manual: Mission Control → Scheduled jobs → Run-now / Pause / Edit-cron on `daily-journal`; set a tenant billing
time in the past and confirm due subscriptions renew; set a low auto-load threshold and record usage to trip it.

## ACCEPTANCE CRITERIA
- [ ] Mission Control lists every job (Service/Cron/Next-fire/Last-run/State) with working Run-now, Pause/Resume,
      Edit-schedule; edits survive restart; the 3 existing jobs + 2 new ones appear.
- [ ] Subscriptions auto-renew once per tenant per day at the tenant's configurable time (default 12:00:00),
      off-session, idempotent, dunning on failure.
- [ ] Metered customers auto-load prepaid credit the moment usage crosses their threshold, off-session, with a
      daily safety-net retry; no duplicate/again-while-pending charges.
- [ ] All ledger invariants hold; all validation commands pass; wiki updated (deployment config, admin
      job-manager, storefront/admin auto-load + billing-time). No AI-authorship trailers; per-PR CI via poller.

## NOTES / decisions to confirm at build time
- **Full control persistence**: a lightweight per-service `ScheduleOverride` table (re-applied at startup) vs.
  enabling Quartz persistent store everywhere. Plan defaults to the override table (uniform, no infra change).
- **Auto-load crediting**: credit-on-webhook-success (safer) vs. optimistic-credit-then-compensate (snappier).
  Plan defaults to credit-on-success + a pending-topup guard for idempotency.
- **Per-tenant time zones**: v1 uses server-time `TimeOnly` per tenant; add IANA tz per tenant later if needed.
- **Confidence for one-pass per-phase**: Phase 2 high; Phase 1 medium (Quartz runtime control + gateway routing);
  Phase 3 medium (prepaid crediting/reconciliation is the subtle part).
