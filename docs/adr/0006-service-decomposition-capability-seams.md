# ADR-0006: Six services cut along business-capability seams

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #6, `docs/prd/3commerce/15-appendix.md`)

## Context

Wrong service boundaries (too fine, or entity-shaped) are the most common microservices failure: they force chatty calls and distributed transactions everywhere. Cart and checkout talk constantly; catalog sync and ordering barely talk.

## Decision

Six services along capability seams: **Identity, Catalog (incl. importers/search), Ordering (incl. cart), Payments (incl. ledger), Fulfillment, Support (incl. RMA)**. Notifications/email is a shared event-consuming worker, not a service. Cart deliberately lives inside Ordering.

## Alternatives considered

- **Coarser (3 services)** — viable fallback (see ADR-0005 contingency) but less learning surface.
- **Finer (9+: cart, pricing, inventory…)** — rejected: every checkout becomes 6 network hops; solo-scale death march.
- **Entity-per-service (ProductService, UserService…)** — rejected: anemic CRUD services with logic in callers — a distributed monolith.

## Consequences

- Splitting cart from checkout is explicitly off the table.
- New capabilities (e.g. promotions) must argue their way into an existing seam before becoming a seventh service.
