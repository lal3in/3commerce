# ADR-0005: Microservices architecture, chosen explicitly for learning value

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #5, `docs/prd/3commerce/15-appendix.md`)

## Context

A solo developer on a greenfield project is the textbook case **against** microservices: the distributed tax (network seams, sagas, eventual consistency, per-service deployment) is paid alone, with none of the organizational benefits. The modular monolith was recommended during the design interview — and declined.

## Decision

Build six microservices anyway: **the distributed-systems learning is the point.** The accepted cost is a 3–5× build premium over a monolith. Compensating disciplines: each service stays internally simple; complexity budget is spent on the seams; phases end at demonstrable milestones.

## Alternatives considered

- **Modular monolith, microservice-ready** — recommended, declined: hides the distributed problems the owner wants to confront.
- **Two-service pragmatic split (API + worker)** — rejected: still effectively a monolith.

## Consequences

- Sagas, outbox, idempotent consumers, and per-service data are mandatory patterns, not options (ADR-0007, ADR-0008).
- Contingency (PRD risk R-1): if solo pace fails, collapse to 3 services (Identity / Commerce / Money) — capability seams make merging cheaper than splitting.
