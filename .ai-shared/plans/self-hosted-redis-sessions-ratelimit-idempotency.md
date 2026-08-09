# Feature: Self-hosted Redis — shared session cache, distributed rate limiting, and idempotency fast-path

The following plan is complete but you MUST validate the cited files/patterns and re-confirm the
"Decisions to confirm" before implementing. Redis is introduced as a **cache / fast-path**, never as a
new source of truth: Postgres stays authoritative for auth and money. Pay attention to key naming, TTLs,
and the fail-open/degrade behaviour when Redis is unavailable.

## Feature Description

Add a self-hosted Redis to the platform and use it for three cross-instance concerns that today are either
in-process (incorrect when scaled out) or on the hot Postgres path:

1. **Sessions / auth (Identity)** — a fast, shared introspection cache + MFA-challenge store across instances.
2. **Rate limiting (Gateway)** — currently in-process `System.Threading.RateLimiting`, so per-replica; make it shared/correct.
3. **Idempotency / webhook dedupe (Payments)** — take the per-request "have I seen this key?" check off hot Postgres.

Plus the observability the platform expects: log verbosity, OTEL metrics, and Grafana dashboard items.

## User Story

As a platform operator running **multiple replicas** of the gateway and services,
I want session introspection, rate limiting, and idempotency/webhook dedupe to be **fast and correct across all instances**,
So that scaling out does not multiply rate limits, hammer Postgres on every request, or lose MFA-challenge state between instances.

## Problem Statement

- **Rate limiting is per-instance.** `src/Gateway/Program.cs:28` builds a `PartitionedRateLimiter` over the in-process
  `System.Threading.RateLimiting` fixed-window limiter. With N gateway replicas the effective limit is ~N× the intended
  (`auth:` 30/min becomes 30N/min) — a real credential-stuffing / abuse gap.
- **Session introspection is a hot Postgres JOIN on every authenticated request.** `AuthService.IntrospectAsync`
  (`src/Services/Identity/Infrastructure/AuthService.cs:244`) joins `Sessions ⋈ Users ⋈ Principals` per request (the
  gateway introspects every call). This scales poorly and couples request latency to Postgres.
- **MFA-challenge / pending state lives only in Postgres** (`Session.MfaPending`, `Session.StrongAuthAt`,
  `src/Services/Identity/Domain/Session.cs`). Fine functionally, but transient challenge state on the durable table is
  extra write load and not designed for short-TTL semantics.
- **Idempotency/webhook dedupe writes to Postgres on every call.** `IdempotencyGuard`
  (`src/Services/Payments/Infrastructure/Idempotency/IdempotencyGuard.cs:14`) does a `FindAsync` + insert against
  `IdempotencyRecord` per operation. For high-volume webhook dedupe this belongs on a TTL'd fast-path.

## Solution Statement

Introduce **one self-hosted Redis** (dev: `docker-compose.infra.yml`; prod: Helm) and a shared
`ThreeCommerce.BuildingBlocks.Infrastructure.Redis` client (`StackExchange.Redis` singleton `IConnectionMultiplexer`)
wired into the existing OTEL pipeline. Apply it in three feature-flagged, independently-shippable phases, each with a
**Postgres fallback** so a Redis outage degrades gracefully rather than failing the platform:

- **Rate limiting** → Redis atomic fixed/sliding-window (Lua) keyed by the existing partition; fail-open on outage.
- **Idempotency / webhook dedupe** → Redis `SET key … NX EX ttl` fast-path in front of the durable `IdempotencyRecord`.
- **Sessions** → cache-aside for `IntrospectAsync` (cache the `SessionInfo` by token hash, TTL-bounded), invalidated on
  logout / revoke / `ClaimsVersion` bump; **MFA challenge** as a short-TTL Redis record shared across instances.

Correctness rule: **Redis holds only token *hashes* and derived/transient data, never raw tokens or money truth.**

## Feature Metadata

**Feature Type**: Enhancement (infrastructure + cross-cutting)
**Estimated Complexity**: High (auth-sensitive; multi-service; new infra + HA/failure semantics)
**Primary Systems Affected**: Gateway, Identity, Payments, BuildingBlocks (new Redis + observability), deploy (compose/Helm), observability (Grafana)
**Dependencies**: `StackExchange.Redis`, `OpenTelemetry.Instrumentation.StackExchangeRedis`, **`valkey/valkey:8-alpine`** (BSD-licensed Redis drop-in — confirmed engine), `oliver006/redis_exporter` (server metrics), Valkey **HA** (Sentinel or Cluster) in prod Helm

> **Confirmed decisions (2026-08-09):** engine = **Valkey** (protocol-compatible with `StackExchange.Redis`; "Redis"
> below means the Valkey server); session model = **cache-aside over Postgres**; topology = **HA from day one**
> (Valkey Sentinel/Cluster in prod; single node acceptable for dev/CI); rate-limit outage behaviour = a **config toggle**
> `RateLimiting:OnRedisOutage = FailOpen|FailClosed` (both implemented, documented in-config).

---

## CONTEXT REFERENCES

### Relevant Codebase Files — READ THESE BEFORE IMPLEMENTING

- `src/Gateway/Program.cs` (lines 28–60, 94) — the in-process rate limiter to replace; keep the partition-key logic
  (tenant:storefront:ip, auth-vs-any) and `RejectionStatusCode` 429; `app.UseRateLimiter()` at line 94.
- `src/Services/Identity/Infrastructure/AuthService.cs` (`IntrospectAsync` ~244; `CreateSession` ~146; `LogoutAsync`
  ~163; `RevokeAll` ~232; MFA methods `CompleteMfaChallengeAsync` ~359, `StepUpAsync` ~414) — session lifecycle to
  cache/invalidate. Note: cache MUST be invalidated wherever `RevokedAt` is set or `ClaimsVersion` changes.
- `src/Services/Identity/Domain/Session.cs` — source-of-truth session; `TokenHash` (SHA-256), `ClaimsVersion`,
  `MfaPending`, `StrongAuthAt`, `IsActive`. Redis keys use the **same token hash**, never the raw token.
- `src/Services/Identity/Api/Endpoints/MfaEndpoints.cs` (`Challenge` ~104) — MFA challenge flow to back with a Redis record.
- `src/Services/Payments/Infrastructure/Idempotency/IdempotencyGuard.cs` + `src/Services/Payments/Domain/IdempotencyRecord.cs`
  + `IIdempotencyGuard.cs` — the durable idempotency store; add a Redis fast-path in front, keep PG as the durable replay store.
  Callers: `src/Services/Payments/Api/Endpoints/AdminEndpoints.cs:91` (and wire the inbound webhook path — see
  `src/BuildingBlocks/Infrastructure/Webhooks/InboundWebhookVerifier.cs` / Payments webhook endpoints).
- `src/BuildingBlocks/Infrastructure/Observability/OtelExtensions.cs` — `AddServiceTelemetry(serviceName)`; add Redis
  instrumentation + register the new Redis `Meter` here (mirror `AddMeter(StreamMetrics.MeterName)`).
- `src/BuildingBlocks/Infrastructure/Streams/StreamMetrics.cs` — the exact `Meter`/`Counter` pattern to mirror for `RedisMetrics`.
- `docker-compose.infra.yml` (postgres/rabbitmq/kafka services + named volumes) — add the `redis` service + volume here.
- `deploy/helm/3commerce/` — add the prod Redis (subchart or Deployment) + `ConnectionStrings:Redis` wiring.
- `deploy/observability/grafana/provisioning/dashboards/*.json` (`services-red.json`, `logs-overview.json`) — the JSON
  dashboard shape to mirror for a new `redis-overview.json`; `deploy/observability/otel-collector-config.yaml` — add the
  redis_exporter scrape.
- `tests/3commerce.IntegrationTests/Phase4Fixture.cs` (shared Testcontainers pattern) — add a Redis testcontainer here
  (mirror the RabbitMQ container) so cross-instance behaviour can be tested.

### New Files to Create

- `src/BuildingBlocks/Infrastructure/Redis/RedisExtensions.cs` — `AddRedis(this IHostApplicationBuilder, cfg)` registering
  a singleton `IConnectionMultiplexer` from `ConnectionStrings:Redis`; no-ops (returns a null-object client) when unset so
  local bare-runs work without Redis.
- `src/BuildingBlocks/Infrastructure/Redis/RedisMetrics.cs` — `Meter "3commerce.redis"` with the counters/histograms below.
- `src/BuildingBlocks/Infrastructure/Redis/IRateLimitStore.cs` + `RedisRateLimitStore.cs` — atomic window via a Lua script.
- `src/BuildingBlocks/Infrastructure/Redis/IDedupeStore.cs` + `RedisDedupeStore.cs` — `SET NX EX` dedupe.
- `src/Services/Identity/Infrastructure/Sessions/ISessionCache.cs` + `RedisSessionCache.cs` — cache-aside for `SessionInfo`.
- `src/Services/Identity/Infrastructure/Mfa/IMfaChallengeStore.cs` + `RedisMfaChallengeStore.cs` — short-TTL challenge record.
- `deploy/observability/grafana/provisioning/dashboards/redis-overview.json` — Grafana dashboard (panels below).
- `docs/adr/0044-self-hosted-redis-cache-ratelimit-idempotency.md` — the ADR (mirror ADR-0043 structure + mermaid).

### Relevant Documentation (read before implementing)

- StackExchange.Redis basics + `ConnectionMultiplexer` lifetime (singleton): https://stackexchange.github.io/StackExchange.Redis/Basics
- Distributed rate limiting with Redis (atomic INCR+EXPIRE / token-bucket Lua): https://redis.io/glossary/rate-limiting/
- OpenTelemetry Redis instrumentation: https://www.nuget.org/packages/OpenTelemetry.Instrumentation.StackExchangeRedis
- redis_exporter (server metrics for Prometheus/Grafana): https://github.com/oliver006/redis_exporter

### Patterns to Follow

- **Telemetry opt-in per service**: every service `Program.cs` calls `builder.AddServiceTelemetry("<name>")`
  (`src/Services/Identity/Api/Program.cs:17`). Add `builder.AddRedis(...)` next to it.
- **Connection strings**: `builder.Configuration.GetConnectionString("Database")` / `"RabbitMq"`; add `"Redis"` the same way.
  Integration tests inject via `builder.UseSetting("ConnectionStrings:Redis", …)` (see `Phase4Fixture.CreateFactory`).
- **Metrics**: mirror `StreamMetrics` — `static Meter`, `CreateCounter`/`CreateHistogram`, register via `AddMeter` in `OtelExtensions`.
- **Money units / correctness posture** (`AGENTS.md`): Redis never becomes the ledger/session truth; PG remains authoritative.

---

## IMPLEMENTATION PLAN

### Phase 0 — Foundation: Redis + client + observability scaffold (no behaviour change)

- Add a `redis` service (image **`valkey/valkey:8-alpine`**) to `docker-compose.infra.yml` (`--appendonly yes`,
  `--requirepass`, port 6379, named volume) — single node for dev/CI. Prod (Helm): **Valkey HA** — Sentinel (1 primary +
  2 replicas + 3 sentinels) or Valkey Cluster; `StackExchange.Redis` connects via the sentinel/cluster endpoint list.
  Wire `ConnectionStrings:Redis` (+ TLS + auth) in `docker-compose.prod.yml` and the chart. Keep the connection abstract
  so dev-single-node vs prod-HA differ only by connection string.
- Create `RedisExtensions.AddRedis` (singleton `IConnectionMultiplexer`; graceful no-op when unset).
- Wire OTEL: `AddRedisInstrumentation()` in `OtelExtensions` (traces) + `AddMeter("3commerce.redis")`; add `OpenTelemetry.Instrumentation.StackExchangeRedis` package.
- Create `RedisMetrics` (counters/histograms). Add `redis_exporter` to compose + a Prometheus scrape in `otel-collector-config.yaml`.
- Add a `redis-overview.json` Grafana dashboard skeleton (populated per phase).
- Add a Redis testcontainer to `Phase4Fixture` (and a Phase-2 fixture for Identity), shared like RabbitMQ.

### Phase 1 — Gateway distributed rate limiting (lowest risk, fixes the per-instance bug)

- Implement `RedisRateLimitStore` (atomic fixed-window via Lua: `INCR` + `EXPIRE` on first hit; or sliding-window log).
  Reuse the existing partition key (`(auth|any):tenant:storefront:ip`) and limits (auth 30/min, any 1000/min).
- Replace the in-process limiter in `Gateway/Program.cs` with a custom middleware/policy backed by `IRateLimitStore`,
  **feature-flagged** (`RateLimiting:Backend = InMemory|Redis`). On a Redis outage the behaviour is a **config toggle**
  `RateLimiting:OnRedisOutage = FailOpen|FailClosed` (both implemented) — log Warning + `redis_unavailable_total` either way.
  The `appsettings.json` block must carry **comments explaining usage**, e.g.:

  ```jsonc
  // Gateway rate limiting. Backend=Redis makes limits correct across replicas (in-process is per-instance).
  "RateLimiting": {
    "Backend": "Redis",          // InMemory = legacy per-instance limiter; Redis = shared/distributed
    "OnRedisOutage": "FailOpen"  // FailOpen = fall back to in-process (don't lock users out on a cache blip);
                                 // FailClosed = reject (429) until Redis recovers (stricter abuse protection)
  }
  ```
- Emit `redis_ratelimit_decisions_total{result,partition_kind}`; keep 429 semantics.

### Phase 2 — Payments idempotency / webhook dedupe fast-path

- Implement `RedisDedupeStore.TrySeenAsync(key, ttl)` (`SET key 1 NX EX ttl` → returns whether it was newly set).
- In `IdempotencyGuard` (and the inbound webhook handler), consult Redis first: unseen → proceed + record in PG (durable
  replay); seen → short-circuit / return stored response from PG. Redis is the fast negative-cache; **PG remains the
  durable replay + conflict store** (`IdempotencyConflictException` unchanged). Degrade to PG-only when Redis is down.
- Emit `redis_idempotency_dedupe_total{result=hit|miss|conflict}`.

### Phase 3 — Identity session introspection cache + MFA challenge store (most sensitive)

- `RedisSessionCache`: cache the `SessionInfo` (already token-hash keyed) with TTL = `min(remaining session TTL, cap)`.
  `IntrospectAsync` becomes cache-aside: hit → return; miss → the existing PG JOIN, then prime cache.
  **Invalidate** the key in `LogoutAsync`, `RevokeAll`, and anywhere `ClaimsVersion` changes; embed `ClaimsVersion` in the
  cached value and re-check against the principal on read (defence-in-depth). MFA-pending sessions are **never** cached.
- `RedisMfaChallengeStore`: short-TTL (≈5 min) challenge record `{ sessionId, userId, attempts }` keyed by challenge id,
  shared across instances; attempt counter + lockout in Redis. PG session stays the durable pending flag.
- Emit `redis_session_cache_{hits,misses}_total`, `redis_mfa_challenge_total{result}`.
- Security review + tests before enabling in prod (feature-flag `Sessions:Cache = off|on`).

### Phase 4 — Docs, ADR, wiki, dashboards, alerts

- ADR-0044; update `docs/help/deployment.md`, `testing.md`, `services.md` (+ nav); optionally a new archify dataflow
  ("request → Redis fast-path → PG fallback"). Finalise the Grafana dashboard + alert rules.

---

## OBSERVABILITY (logs + metrics + Grafana)

### Log verbosity (per environment)

- New log categories: `ThreeCommerce.Redis` (client/connection), `ThreeCommerce.Gateway.RateLimiting`,
  `ThreeCommerce.Payments.Idempotency`, `ThreeCommerce.Identity.SessionCache`, `ThreeCommerce.Identity.Mfa`.
- Levels: connection up/reconnect → **Information/Warning**; cache hit/miss → **Debug** (dev) / suppressed (prod);
  rate-limit **reject** → **Information** (partition, tenant, ip); idempotency **conflict** → **Warning**;
  **Redis unavailable / fallback engaged** → **Warning** (+ Error if the whole multiplexer is down).
- `appsettings.json` `Logging:LogLevel` overrides: `StackExchange.Redis` = `Warning` (avoid chatter); our `ThreeCommerce.*`
  Redis categories = `Debug` in `appsettings.Development.json`, `Information` in prod. Logs already flow to Loki via
  `AddServiceTelemetry` (OTLP), so no new pipeline — just the category levels.

### Metrics (`RedisMetrics`, Meter `3commerce.redis`, registered via `AddMeter`)

- `redis_ratelimit_decisions_total{result=allow|reject, partition_kind=auth|any}`
- `redis_idempotency_dedupe_total{result=hit|miss|conflict}`
- `redis_session_cache_hits_total` / `redis_session_cache_misses_total`
- `redis_mfa_challenge_total{result=issued|verified|expired|locked}`
- `redis_operation_duration_seconds` histogram `{op}`
- `redis_unavailable_total{concern=ratelimit|idempotency|session}` (fallback engaged)
- Server-level via **redis_exporter** → Prometheus: memory, evictions, connected clients, keyspace hits/misses, uptime.

### Grafana (`redis-overview.json`, provisioned like the existing dashboards)

Panels: session cache **hit ratio**; rate-limit **allow vs reject** by partition_kind; idempotency dedupe/conflict; MFA
challenge outcomes; Redis op latency **p50/p95/p99**; **Redis up + reconnects**; memory/evictions/keyspace (exporter);
`redis_unavailable_total` (fallback engaged). **Alerts**: Redis down; reject-rate spike; cache-hit-ratio drop; eviction spike.

---

## TESTING STRATEGY

- **Unit**: `RedisRateLimitStore` window math + expiry; `RedisDedupeStore` NX semantics; `RedisSessionCache` invalidation
  on logout/revoke/ClaimsVersion bump and MFA-pending-never-cached; MFA challenge TTL + attempt lockout; fallback paths
  when the client is the no-op/unavailable variant.
- **Integration (Testcontainers Redis in `Phase4Fixture`/Identity fixture)**: (1) two gateway factories sharing one Redis
  enforce **one** shared limit (regression for the per-instance bug); (2) logout on instance A invalidates the introspection
  cache seen by instance B; (3) a replayed webhook is deduped across instances; (4) **Redis-down** → each concern degrades
  (rate-limit fail-open, idempotency/session fall back to PG) with the `redis_unavailable_total` metric incrementing.
- **Edge cases**: Redis restart mid-session (cache miss → PG rehydrate); clock skew on TTL; oversized `SessionInfo`;
  eviction under `maxmemory` (all keys carry TTLs; policy `volatile-lru`); conflicting idempotency key still throws.

## VALIDATION COMMANDS

- L1 style/build: `dotnet build 3commerce.sln` (0 warnings) + `dotnet format --verify-no-changes` on touched projects.
- L2 unit: `dotnet test 3commerce.sln --filter Category!=Integration`.
- L3 integration: `dotnet test tests/3commerce.IntegrationTests --filter Category=Integration` (Docker up; Redis container).
- L4 manual: `scripts/run-all.sh start` with Redis in the infra compose; hit an auth endpoint past the limit from two
  gateway instances (shared 429); confirm Grafana `redis-overview` panels populate; `scripts/verify-golive-gate.sh` still green.
- L5 regression: `scripts/e2e-verify.sh` unaffected.

## ACCEPTANCE CRITERIA

- [ ] Redis self-hosted in dev compose + prod Helm, auth/TLS in prod, all keys TTL'd.
- [ ] Rate limiting is correct across N gateway replicas (shared limit), fail-open on Redis outage.
- [ ] `IntrospectAsync` served from cache on hit; correctly invalidated on logout/revoke/ClaimsVersion; MFA-pending never cached.
- [ ] MFA challenge state shared across instances with TTL + attempt lockout.
- [ ] Webhook/idempotency dedupe fast-path on Redis with durable PG replay + conflict behaviour preserved.
- [ ] Redis is never the source of truth; every concern degrades to PG/in-process when Redis is down.
- [ ] Logs at the specified levels; `3commerce.redis` metrics exported; `redis-overview.json` dashboard + alerts provisioned.
- [ ] ADR-0044 + wiki updated; all suites green; no security regression (token hashes only; no raw tokens in Redis).

---

## DECISIONS (confirmed 2026-08-09)

1. **Rate-limit on Redis outage** → a **config toggle** `RateLimiting:OnRedisOutage = FailOpen|FailClosed` (both built;
   default FailOpen; commented in `appsettings.json` — see Phase 1).
2. **Session store model** → **cache-aside over Postgres** (PG stays source of truth; Redis caches introspection).
3. **Redis topology** → **HA from day one**: Valkey Sentinel (or Cluster) in prod Helm; single node for dev/CI. PG/in-process
   fallback still applies (HA reduces outage frequency; the degrade paths remain the safety net).
4. **Engine** → **Valkey** (`valkey/valkey:8-alpine`), BSD-licensed Redis drop-in; `StackExchange.Redis` unchanged.
5. **Eviction/persistence** → `appendonly yes` + `maxmemory-policy volatile-lru`, all keys TTL'd (safe: reconstructable from PG).

## NOTES

- Ship the three concerns **independently** behind flags; Phase 1 (rate limiting) delivers the clearest correctness win at
  the lowest blast radius, Phase 3 (sessions/MFA) is the most auth-sensitive and gated on a security review + tests.
- **HA scope**: because topology is HA from day one, Phase 0 provisions Valkey Sentinel/Cluster in the chart and the
  integration tests should also cover **failover** (primary drops → sentinel promotes a replica → client reconnects), not
  just single-node outage. Server-level HA metrics (sentinel/replica state, replication lag) go on the Grafana dashboard.
- Keep `deploy/pgbouncer` in mind: moving read load off PG for introspection complements existing PG pooling.

**Confidence for one-pass success per phase: 8/10** (Phase 1 highest; Phase 3 lowest — auth-sensitive invalidation).
