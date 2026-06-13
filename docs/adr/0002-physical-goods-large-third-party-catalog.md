# ADR-0002: Sell physical goods from large third-party catalogs

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #2, `docs/prd/3commerce/15-appendix.md`)

## Context

The product category determines whether inventory, shipping, customs, and returns exist at all, and what "refund" means operationally.

## Decision

The store sells **physical goods** sourced from **large third-party catalogs** (thousands of SKUs). The hard problem is therefore catalog ingestion/sync and fulfillment against external data, not just the storefront.

## Alternatives considered

- **Small own catalog** — rejected: not the owner's business direction.
- **Digital goods** — rejected as primary line (note: this choice later ruled out Polar as a payment rail, see ADR-0014).
- **Services/subscriptions** — rejected: different domain model entirely.

## Consequences

- Stock/shipping/customs are in the domain; search at multi-thousand-SKU scale is a feature (ADR-0020).
- Supplier data quality becomes a first-class operational concern (import monitoring in admin).
