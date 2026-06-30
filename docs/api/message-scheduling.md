# Message Scheduling Policy

Scheduling is split by operational intent. RabbitMQ/MassTransit owns workflow-local delayed messages; Quartz owns durable recurring or calendar-like jobs. Kafka never schedules work; it only receives committed facts such as `workflow.runs`.

## Decision table

| Use case | Mechanism | Owner | Idempotency key | Notes |
|---|---|---|---|---|
| Saga timeout, compensation window, delayed redelivery | RabbitMQ/MassTransit scheduling/delayed redelivery | Service that owns the saga | Saga id + message id | Keep close to the operational workflow and retry policy. Do not move checkout/RMA/payment commands to Kafka. |
| Short retry for a transient command/event consumer failure | MassTransit retry + delayed redelivery | Consuming service | Inbox/message id | Prefer built-in bus retry and error queues. |
| Recurring operational job, e.g. nightly sync/export/feed | Quartz persistent scheduler | Workflow for central jobs; service-local only for simple non-critical jobs | `jobName:scheduledFireTime` or domain reference | Persistent AdoJobStore, clustering, and explicit misfire policy required for production-critical jobs. |
| Retry sweep for webhooks/exports/outbound jobs | Quartz persistent scheduler | Workflow or owning dispatcher service | Delivery/export id + scheduled fire time | The sweep finds durable work rows; it must be safe to run twice. |
| Billing period close / usage rollup | Quartz persistent scheduler | Workflow orchestrates; Usage/Payments own state changes | Tenant + period + job name | Job may publish commands over RabbitMQ; committed billing/ledger facts may later stream to Kafka. |
| Analytics/replay/read-model rebuild | Kafka consumer | Projection owner | Stream `eventId` or projection watermark | This is consumption, not scheduling. Offset commits alone are insufficient. |

## Rules

- Use RabbitMQ/MassTransit for operational commands, sagas, retries, delayed redelivery, and request/reply style workflows.
- Use Quartz for typed recurring jobs that must survive process restarts, deploys, or multi-replica failover.
- Do not define SQL/shell jobs from admin UI; jobs are typed code only.
- Production-critical Quartz jobs must use persistent clustered storage, not RAMJobStore.
- Misfires must be explicit. Current recurring jobs use `DoNothing`, so missed ticks are not replayed automatically unless the job implements a durable catch-up sweep.
- Jobs must be idempotent by `jobName:scheduledFireTime` or a stronger domain id such as billing period, webhook delivery id, export id, or journal reference.
- Kafka topics may receive `workflow.runs` facts after a job run is committed, but Kafka does not trigger authoritative operational changes.

## Current implementation

- `AddScheduledJobs(configuration, ...)` supports optional Quartz PostgreSQL AdoJobStore, clustering, `AUTO` scheduler instance ids, and configurable misfire/check-in thresholds.
- `JobExecutor` records each run and publishes `JobRunRecorded` to the Workflow projection when a bus publisher is wired.
- Workflow is the intended owner for production-critical central schedules; service-local schedules remain acceptable for simple service-owned jobs until they become launch-critical.
