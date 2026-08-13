# Scheduled jobs

Recurring background work — posting the daily ledger journal, renewing subscriptions,
closing usage periods, going live with scheduled content — runs as **cron-triggered
scheduled jobs**. Each job lives in the service that owns its data; the Admin
**Mission Control** page is the single manager where you see every job, run one on
demand, pause/resume it, or change its schedule.

> **Where:** Admin → **Mission Control** → *Scheduled jobs*
> (`src/Admin/Components/Pages/MissionControl.razor`). The `admin` role is required.
> Mission Control reads each owning service's live schedule directly and enriches it
> with run history aggregated by the **Workflow** service.

## The jobs that exist today

| Service | Job | Schedule (cron) | What it does |
|---|---|---|---|
| marketing | `scheduled-publish` | `0 * * * * ?` — every minute | Applies due **content publishes**: anything whose scheduled go-live time has arrived is published (latency ≤ 60s). No-op when nothing is due. |
| payments | `subscription-auto-renew` | `0 0/15 * * * ?` — every 15 min | Sweeps **due subscription renewals** and charges them off-session; each storefront renews at most once/day once its window passes; a failed charge marks past-due (dunning). Idempotent. |
| payments | `daily-journal` | `0 0 2 * * ?` — 02:00 UTC daily | Posts the prior UTC day's **ledger journal to Xero**. Idempotent per date. |
| usage | `usage-period-close` | `0 0 * * * ?` — hourly | Closes **metered usage periods** that have ended: bills unbilled overage and rolls each balance to its next period. Idempotent. |

The **workflow** service registers the scheduler too but owns no jobs — it aggregates
every service's run history (`JobRunRecorded` events) so Mission Control shows one list.

## How a job runs

1. **Trigger** — Quartz fires the cron trigger (or you press *Run now*). A persistent,
   clustered Quartz store can be enabled so schedules survive restarts and only one node
   fires in a cluster.
2. **Execute** — the job resolves in a fresh per-fire DI scope and `JobExecutor` runs its
   `ExecuteAsync`; transient failures are retried up to a bounded count.
3. **Record** — every fire is written to the owning service's local `JobRuns` table and a
   `JobRunRecorded` event is published so Workflow projects it into the shared history.
4. **Surface** — Mission Control renders each service's live schedule (*Next fire*, paused
   state) together with the Workflow run history (*Last run* status + duration).

Every job is **idempotent** — *Run now* and retries never double-charge or double-post, so
running a job on demand is a safe operator action.

## Using the jobs from Mission Control

- **Run now** — triggers the job immediately, regardless of cron (and even if paused).
  Safe because jobs are idempotent; the row's *Last run* updates once it records.
- **Pause / Resume** — *Pause* stops the cron from firing it (without removing it); it can
  still be run with *Run now*. The paused state persists across restarts.
- **Edit** — change the **cron** inline and **Save**. Standard 6-field Quartz cron
  (`sec min hour day month day-of-week`), e.g. `0 0 2 * * ?` = 02:00 daily. Invalid cron is
  rejected.

## Adding a job

1. **Implement `IScheduledJob`** (`src/BuildingBlocks/Infrastructure/Scheduling/ScheduledJob.cs`):
   a `Name`, a default `CronSchedule`, and an idempotent `ExecuteAsync`.
2. **Register it** in the owning service's `Program.cs`:
   `builder.Services.AddScheduledJobs(builder.Configuration, "usage", jobs => jobs.Add<MyJob>("my-job", "0 0 * * * ?"));`
3. **Persist run history** — the service's `DbContext` calls `ConfigureJobRuns()` (and
   `ConfigureScheduleOverrides()`), register `EfJobRunStore<MyDbContext>`, expose
   `app.MapJobControl()`, and add the service to `MissionControl.razor`'s `JobServices` list.

> **Outbox gotcha:** the run-history event goes through the EF outbox, which only sends on
> the store's `SaveChanges`. `JobExecutor` publishes `JobRunRecorded` **before** its final
> save so it flushes — a run that never appears in Mission Control is usually a broken
> publish-then-save order or a missing `ConfigureJobRuns`.

## Removing or disabling a job

- **Temporarily** — *Pause* it in Mission Control (no deploy).
- **Permanently** — delete its `.Add<MyJob>(…)` registration (and the job class) and
  redeploy. Historical `JobRuns` rows remain for the audit trail.

## API surface

All via the gateway (`admin` role), forwarded to the owning service:

| Purpose | Call |
|---|---|
| List live schedule | `GET /api/{service}/admin/jobs` |
| Run now | `POST /api/{service}/admin/jobs/{name}/run` |
| Pause / Resume | `POST …/{name}/pause` · `…/resume` |
| Reschedule | `PUT …/{name}/schedule` with `{ cron }` |
| Aggregated run history | `GET /api/workflow/admin/workflow/runs` |
