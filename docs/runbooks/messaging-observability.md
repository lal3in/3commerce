# Messaging Observability Runbook

This runbook covers the RabbitMQ operational bus, Kafka durable stream lane, stream relay, DLQ, and Quartz scheduler signals from ADR-0034.

## Metrics emitted by application code

The shared stream infrastructure emits the `3commerce.streams` meter:

| Metric | Labels | Meaning | Alert idea |
|---|---|---|---|
| `stream_events_published_total` | `topic` | Producer publish acknowledgements, including fake/dev producer | Sudden drop to zero on active topics during business hours |
| `stream_events_consumed_total` | `topic`, `result` | Consumer processing result: `Processed`, `Duplicate`, `DeadLettered` | DeadLettered > 0 over 5 minutes |
| `stream_events_dead_lettered_total` | `topic`, `error_type` | Poison/schema/handler failures sent to DLQ | Any sustained non-zero rate |
| `stream_outbox_relay_published_total` | `topic` | Stream outbox rows successfully relayed | Relay flatlines while unpublished rows grow |
| `stream_outbox_relay_failed_total` | `topic` | Relay failures with backoff recorded on the row | Any sustained non-zero rate |

OpenTelemetry registers this meter in `AddServiceTelemetry`; metrics export through OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured.

## Broker and scheduler signals to dashboard

| Area | Signal | Source | First response |
|---|---|---|---|
| RabbitMQ | Queue depth, unacked messages, redeliveries, error queue depth | RabbitMQ management/prometheus exporter | Inspect first failing consumer logs; do not purge queues until poison messages are understood. |
| Kafka | Consumer group lag per topic/partition | Managed Kafka metrics or Kafka exporter | Check relay/consumer health, broker reachability, and DLQ rate. |
| Stream relay | Unpublished row count by topic and max row age | SQL probe over `StreamOutboxMessages` | If broker is down, wait for recovery; if payload is poison, inspect `LastError` and DLQ/operator replay path. |
| Stream DLQ | DLQ topic volume and error type | App metric + Kafka topic | Triage schema changes vs handler bug. Replay only after a code/data fix. |
| Quartz | Misfire count, scheduler instance check-ins, failed job runs | Quartz tables + Workflow `/admin/workflow/runs` projection | Ensure only clustered schedulers are active and job idempotency key prevents duplicate effects. |

## Stream outbox SQL probes

Run against the owning service database/schema:

```sql
select "Topic", count(*) as unpublished, min("AvailableAt") as oldest_available
from <schema>."StreamOutboxMessages"
where "PublishedAt" is null
group by "Topic"
order by unpublished desc;

select "Topic", "PublishAttempts", left("LastError", 200) as last_error, count(*)
from <schema>."StreamOutboxMessages"
where "PublishedAt" is null and "PublishAttempts" > 0
group by "Topic", "PublishAttempts", left("LastError", 200)
order by "PublishAttempts" desc;
```

## Operating rules

- Kafka outage must not block checkout/payment/RMA/fulfillment workflows; those remain on RabbitMQ/MassTransit.
- A growing stream outbox is acceptable during Kafka downtime if core service health remains green and rows publish after recovery.
- Do not manually mark stream rows published unless the matching Kafka event is confirmed by topic/key/event id.
- DLQ replay requires a documented fix, operator note, and idempotency check by `eventId`.
- Quartz job failures should appear both in service logs and the Workflow run projection.
