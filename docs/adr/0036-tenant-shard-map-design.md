# ADR-0036: Tenant shard-map design

Status: Accepted
Date: 2026-06-28
Area: Multi-tenancy / data / scale

## Context

3commerce is strictly multi-tenant: a tenant is one legal operating business, tenant-owned rows carry `TenantId`, and PostgreSQL RLS is the first isolation layer inside each service database.

The current deployment uses one logical database per DB-owning service on one PostgreSQL instance. This is the correct MVP/early-production shape. Future production volume may require spreading tenants across regions or shard groups, but enabling sharding too early would complicate every data path.

## Decision

Adopt a future-ready shard-map model, but do not implement runtime sharding yet.

The conceptual placement record is:

```text
tenantId -> region -> shardGroup -> service database endpoint(s)
```

A shard group contains one database endpoint per DB-owning service for that tenant placement. Service-owned schemas and RLS remain unchanged inside each shard.

## Design rules

- Tenant placement is immutable by default after production launch. Moving tenants is a planned migration, not an online background assumption.
- New tenants land on the default shard group until metrics justify multiple groups.
- No cross-shard joins. Cross-service data still moves through APIs/events, never database reads.
- RLS remains mandatory inside each shard; sharding is a scale/region placement layer, not an authorization layer.
- Global secret-keyed lookups (sessions, reset tokens, service tokens) must remain resolvable before tenant location is known. Options are:
  - keep secret-keyed identity lookup tables in a global Identity store; or
  - use a small global locator keyed by cryptographic token hash that returns tenant/shard placement.
- Operational outboxes/inboxes, stream outboxes, audit logs, Quartz/job-run tables, and read models live with the owning service's tenant shard unless explicitly platform-global.
- Kafka stream partition keys remain tenant-aware (`tenantId:aggregateId`) and do not expose shard ids as domain data.

## Not implemented now

This ADR does not add:

- shard-map tables;
- runtime connection routing;
- cross-region replication;
- tenant migration tooling;
- per-tenant data export/import jobs;
- application-level shard selection middleware.

## Future implementation sequence

1. Add a platform-owned tenant placement table/API with `tenantId`, `region`, `shardGroup`, state, and version.
2. Add connection-resolution seams per service host while retaining the current single-shard default.
3. Prove global Identity/session lookup still works before tenant location is known.
4. Add tenant creation placement logic for new tenants only.
5. Add operational dashboards for shard saturation, tenant counts, connection pools, and per-shard lag.
6. Only after production metrics justify it, add migration tooling for tenant moves.

## Consequences

Positive:

- Clarifies the future shape without imposing sharding complexity today.
- Preserves service-owned data boundaries and RLS.
- Keeps Kafka and RabbitMQ contracts tenant-aware without coupling them to physical shards.

Negative:

- A future tenant move remains a deliberate project.
- Global secret-keyed lookup design must be revisited before runtime sharding is enabled.

## Validation

Design validation only:

```bash
dotnet test 3commerce.sln --filter Tenancy
scripts/e2e-verify.sh
```
