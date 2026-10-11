# 0061 — Services wait for their bus before serving; an unreachable broker fails the start

Status: Accepted — implemented
Area: Messaging / hosting / readiness
Extends: [0009](./0009-local-dev-bare-dotnet-run.md) (bare-run dev), [0021](./0021-containerized-launch.md) (containerized launch), [0060](./0060-unique-receive-endpoint-per-service.md)

## Context

Every service and the Notifications worker build their bus with `AddServiceBus`
(`BuildingBlocks/Infrastructure/Messaging/MassTransitExtensions.cs`). It never set
`MassTransitHostOptions.WaitUntilStarted`, so MassTransit 8.5.10's `MassTransitHostedService.StartAsync` returned at
once and started the bus in the background (confirmed by decompiling it, PR #284). The host then started
Kestrel, `/health/ready` answered, and the service published (EF outbox) and expected deliveries before its own
receive endpoints existed.

- On a **fresh broker** (first deploy, a wiped or replaced RabbitMQ) a message published to an exchange with no
  queue bound yet is dropped by RabbitMQ. Nothing errors. The integration suite hit exactly this
  (`MockEmailCaptureTests`, PR #284) and compensated in its fixtures (`TestBusHosting.WaitForBusStartup`).
- A stop requested while the background start was in flight was ignored (`Failed to stop bus … (Not Started)`)
  and the bus finished starting afterwards: a zombie consumer (PR #285).
- With RabbitMQ down, a service still came up and reported "listening"; the bus retried forever in the
  background while `/health/ready` stayed 503, with nothing forcing anyone to notice.

## Decision

1. `AddServiceBus` (both overloads — EF outbox and worker) sets `WaitUntilStarted = true`. `Host.StartAsync`
   returns only once every receive endpoint's queue is declared, bound and consuming. `WebApplicationBuilder`
   registers Kestrel's hosted service last, so **a service does not accept connections until its bus is up**.
   A SIGTERM during boot cancels the start and stops the bus (no zombie).
2. The start is **bounded**: `Messaging:StartTimeoutSeconds` (env `Messaging__StartTimeoutSeconds`), default
   **120 s**; a non-positive value is refused at startup. MassTransit keeps retrying the broker connection
   until the bound; then it stops the half-started bus and the host's start throws
   `RabbitMqConnectionException: Broker unreachable …` — **the process exits non-zero (fail fast)**.
   - Compose: services are `restart: unless-stopped` and `depends_on: rabbitmq: service_healthy`; their
     healthcheck already allows 25 s + 12 × 10 s.
   - Kubernetes/Helm: the pod restarts with back-off (no liveness probe, so a slow boot is never killed); the
     readiness probe allows 10 s + 30 × 5 s.
   - Bare-run: no supervisor, but `dev-up.sh` starts no service before the infra (RabbitMQ included) is healthy.
   120 s rides out a RabbitMQ that is still booting (cold container, new pod) while still surfacing a broker that
   is really gone as a crash with a named cause instead of a silently half-working service.
3. No `StopTimeout`: the host's shutdown token (and the orchestrator's grace period) already bound the stop.
4. Readiness: `/health/ready` already includes MassTransit's bus check (tag `ready`); unchanged. What changes is
   that "not started yet" is now "not listening" (`000`), not `503`. `dev-up.sh` replaces its fixed `sleep 12`
   snapshot with a bounded poll of every service's `/health/ready` (`DEV_UP_SERVICE_WAIT`, default 150 s) before
   seeding, and counts a service that never answers 200 as NOT READY (exit 3). `doctor.sh` / `host-check.sh`
   explain `000`.

Rejected: an unbounded wait (keeps the old "retry forever" behaviour but hides a dead broker behind a port that
never opens); keeping the background start and gating only `/health/ready` (readiness already did that — the
lost-publish and zombie problems are about the process itself publishing and stopping, not about traffic).

## Consequences

- Correct by construction on a fresh broker: no service publishes or reports ready before its queues are bound.
- Startup takes the bus start (~1–3 s with a healthy local broker) before Kestrel listens.
- A broker outage longer than 120 s at boot is a crash loop in containers and a down service in bare-run, with
  `Broker unreachable` in the log. A broker outage after start is unchanged (MassTransit reconnects; readiness 503).
- Test fixtures no longer configure the option (`TestBusHosting.WaitForBusStartup` removed);
  `BusStopsBeforeHostDisposal` and `TestHostTracker` (the #285/#286 guards) stay — they cover the stop path.
- Pinned by `BusStartupTests` (unit lane, e2e-verify A3f) and `BusStartupIntegrationTests` (A4a: publish right
  after start on a fresh broker is consumed; a host started before its broker waits, then serves).
