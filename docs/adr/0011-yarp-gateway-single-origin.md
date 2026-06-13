# ADR-0011: YARP gateway as the single public origin

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #11, `docs/prd/3commerce/15-appendix.md`)

## Context

Six services cannot each be exposed to browsers (six attack surfaces, auth duplicated six times, CORS misery). Something must sit in front, and it must be able to run custom session-validation logic (ADR-0012).

## Decision

A thin ASP.NET service using **YARP** is the single public API origin: routes `/api/{service}/*`, validates the session cookie, mints internal claims, applies rate limiting, strips internal headers from inbound requests. Services and RabbitMQ bind to localhost/private network only.

## Alternatives considered

- **Next.js server as BFF** — rejected: routing/auth/rate-limiting logic migrates into TypeScript, against project goals.
- **Infra-only gateway (nginx/Traefik/Ingress)** — rejected as the *only* layer: cannot run custom token validation; may still front YARP at deploy time.
- **Per-service public exposure + CORS** — rejected outright: contradicts the "secure" requirement.

## Consequences

- The gateway is a deliberate chokepoint: auth, rate limits, and header hygiene live in exactly one place.
- Gateway availability is critical path; it stays thin (no business logic).
