# ADR-0016: "Global from day one" = ship worldwide DAP, tax presence at home only

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #16, `docs/prd/3commerce/15-appendix.md`)

## Context

The owner wants to sell globally from day one. For physical goods, full global commerce means customs paperwork, IOSS/VAT registrations, multi-currency pricing, and carrier contracts per region — team-scale work. There is a lean pattern small stores actually use.

## Decision

**Destination-global, tax-local:** ship worldwide under **DAP** (Delivered At Place — the customer owes import duties), charge home-country tax or zero-rate exports per the (future) home regime, one currency at checkout. Tax registrations expand only when revenue in a region forces it.

## Alternatives considered

- **Full multi-jurisdiction tax registrations day one** — rejected: a tax problem before a revenue problem.
- **Home region only** — rejected: owner explicitly wants worldwide reach.

## Consequences

- Checkout must clearly disclose that import duties/fees are the customer's responsibility.
- Multi-currency display, then pricing, are analytics-triggered future items (PRD §13).

## Status / implementation note (2026-07-11)

The "one currency at checkout" simplification moved on: tenants now set per-currency shelf prices
and each storefront is configured with a currency + tax regime/rate (ADR-0038) — a cart is still
single-currency, and checkout charges the storefront-configured tax (inclusive AU GST / EU VAT,
exclusive US sales tax). DAP shipping and expand-registrations-on-revenue remain the posture.
