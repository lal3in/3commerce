# ADR-0015: No legal entity yet — test mode + configuration seams, build never blocks

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #15, `docs/prd/3commerce/15-appendix.md`)

## Context

The business is **not registered anywhere yet**. Stripe live keys, a production Xero org, payout currency, tax regime, and legal pages all depend on the registration country — which is unknown.

## Decision

Registration is a **launch gate, not a build gate**. Everything jurisdiction-dependent is isolated behind configuration or interfaces:

- Stripe runs **test mode only**; Xero against a **demo org**.
- Currency is a single config value (`STORE_CURRENCY`) — never hardcoded.
- Tax computation sits behind **`ITaxStrategy`** (v1: configurable flat placeholder; swap target: Stripe Tax / OSS logic post-registration).

## Alternatives considered

- **Pretend a jurisdiction and hardcode it** — rejected: wrong guesses become migration debt in money code.
- **Pause until registered** — rejected: the learning goal doesn't wait on paperwork.

## Consequences

- Launch checklist (PRD Appendix B): registration → live keys, real Xero org, real tax strategy, privacy policy/imprint, pen test.
- If registration never happens, the project still succeeds as a portfolio/learning artifact (PRD risk R-4).
