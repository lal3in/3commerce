# ADR-0020: Search via Postgres FTS + pg_trgm behind ISearchProvider

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #20, `docs/prd/3commerce/15-appendix.md`)

## Context

Thousands of third-party SKUs with messy supplier titles make "find the product" a feature. Plain `LIKE` fails customers at this scale; dedicated engines add infrastructure state before there are users.

## Decision

v1 search lives in the Catalog service on the Postgres already running: **tsvector full-text search** (weighted title/brand/description) + **pg_trgm** for typo tolerance + **JSONB attribute filters** + category scoping — comfortably fast at tens of thousands of SKUs. It sits behind an **`ISearchProvider`** interface; the product events already on RabbitMQ (`ProductUpserted`, …) can feed an external engine later without redesign.

## Alternatives considered

- **Meilisearch/Typesense day one** — rejected: visibly nicer instant-search, but another stateful system to sync/back up pre-revenue. Trigger to adopt: relevance/facet complaints or catalog ≫ 100k SKUs.
- **Elasticsearch/OpenSearch** — rejected: JVM-heavy yacht for a pond at this scale.
- **SQL LIKE + category filters** — rejected: fails at thousands of messy SKUs.

## Consequences

- Search performance target: p95 < 500 ms at 10k SKUs (NFR-5), measured in Phase 2.
- Relevance tuning happens in SQL/index land first; engine swap is an event-consumer away.
