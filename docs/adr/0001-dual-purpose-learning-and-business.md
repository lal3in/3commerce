# ADR-0001: Dual purpose — learning vehicle AND real business

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #1, `docs/prd/3commerce/15-appendix.md`)

## Context

E-commerce is a solved problem; building one from scratch needs a justification. The owner wants both to deeply learn distributed-systems engineering on .NET and to end up with a launchable store. Off-the-shelf platforms (Shopify, Medusa) serve the business goal but defeat the learning goal; a pure toy project serves learning but produces nothing launchable.

## Decision

The project pursues both goals simultaneously. Consequence: **no throwaway code** — everything is written, tested, and reviewed as production code, even while payments run in test mode.

## Alternatives considered

- **Prototype/throwaway build** — rejected: deletes the business goal.
- **Shopify / headless SaaS** — rejected: deletes the learning goal.

## Consequences

- Timelines stretch; quality bars (tests, security audits) apply from day one.
- Every later architectural decision must be defensible for real traffic, not just demos.
