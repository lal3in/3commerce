# ADR-0027: Entity service master-data boundary

## Status

Accepted

## Context

The multi-tenant platform needs a canonical place for tenant-scoped parties: companies,
natural persons, trusts, suppliers, couriers, warehouses, customer entity links, and other
legal/profile records. Existing services should not each invent their own legal-entity
schemas or query each other's databases.

## Decision

Create a new Entity service with its own `entity_db` database and `entity` named schema.
The service owns legal/profile master data, entity relationships, identifiers, contact
methods, address versions, supplier onboarding profile state, and duplicate-warning state.
It uses the same MassTransit EF outbox/inbox and PostgreSQL RLS tenant-isolation pattern
as the rest of the platform.

Payments remains the owner of bank accounts, payout instructions, and ledger facts.
Fulfillment remains the owner of inventory locations, carrier config, shipment execution,
and operational stock state. Other services refer to Entity-owned records by ID and keep
snapshots/read models only via APIs or events; they never join Entity tables directly.

## Consequences

- Entity service becomes a prerequisite for supplier, warehouse, courier, and tenant-owner
  master-data flows.
- Later phases can link operational records to `EntityId`/address-version IDs without
  duplicating legal data.
- Sensitive fields still require service-side PEP enforcement and audit coverage in later
  phases.
- Container/Helm/migration wiring must include the seventh DB-owning service.
