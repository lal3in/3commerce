# ADR-0004: Neutral catalog schema + ISupplierImporter + seeded sample data

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #4, `docs/prd/3commerce/15-appendix.md`)

## Context

No supplier contract exists yet, so there is no real feed (API, CSV, EDI) to build against — but the build must not block on the business.

## Decision

Catalog is built on a **neutral internal product schema** (products → variants → categories, JSONB attributes, `supplier_ref` JSONB for source-specific data). Ingestion goes through an `ISupplierImporter` interface; the only v1 implementation is a sample-data generator seeding ≥ 10,000 SKUs. Import runs are persisted (read/accepted/rejected counts) for admin monitoring.

## Alternatives considered

- **Wait for a supplier** — rejected: blocks all catalog work indefinitely.
- **Scraping other stores** — rejected outright: ToS/legal risk, no purchase channel, price drift.
- **Designing around a guessed supplier format** — rejected: wrong guesses become schema debt.

## Consequences

- Real suppliers plug in later as new `ISupplierImporter` implementations.
- Sample data must be realistic enough to exercise search, pricing, and import-rejection paths.
