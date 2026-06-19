# ADR Index

Architecture Decision Records for 3commerce. ADRs 0001–0020 were backfilled on 2026-06-12 from the PRD design interview (`docs/prd/3commerce/15-appendix.md`, Appendix A). New ADRs continue the numbering.
| ADR | Title | Status | Area |
|-----|-------|--------|------|
| [0001](./0001-dual-purpose-learning-and-business.md) | Dual purpose — learning vehicle AND real business | Accepted | Project posture |
| [0002](./0002-physical-goods-large-third-party-catalog.md) | Sell physical goods from large third-party catalogs | Accepted | Product |
| [0003](./0003-per-line-item-fulfillment-source.md) | Fulfillment source tracked per order line item | Accepted | Domain model |
| [0004](./0004-neutral-catalog-schema-supplier-importer.md) | Neutral catalog schema + ISupplierImporter + seeded sample data | Accepted | Catalog |
| [0005](./0005-microservices-architecture.md) | Microservices architecture, chosen explicitly for learning value | Accepted | Architecture |
| [0006](./0006-service-decomposition-capability-seams.md) | Six services cut along business-capability seams | Accepted | Architecture |
| [0007](./0007-masstransit-rabbitmq-outbox-sagas.md) | MassTransit + RabbitMQ, async-first, EF outbox, saga orchestration | Accepted | Messaging |
| [0008](./0008-database-per-service-single-postgres.md) | One Postgres container, one database per service | Accepted | Data |
| [0009](./0009-local-dev-bare-dotnet-run.md) | Local dev = bare `dotnet run`; containers for infra only | Accepted | Dev experience |
| [0010](./0010-nextjs-ssr-storefront.md) | Next.js (SSR) storefront | Accepted | Frontend |
| [0011](./0011-yarp-gateway-single-origin.md) | YARP gateway as the single public origin | Accepted | Edge / security |
| [0012](./0012-custom-auth-opaque-cookie-internal-claims.md) | Custom auth — opaque cookie at edge, signed claims internally | Accepted | Security |
| [0013](./0013-guest-checkout-optional-accounts.md) | Guest checkout + optional accounts; MFA/social deferred | Accepted | Identity / UX |
| [0014](./0014-stripe-only-v1-double-entry-ledger.md) | Stripe-only v1 behind IPaymentProvider; double-entry ledger as truth | Accepted | Payments |
| [0015](./0015-no-legal-entity-test-mode-config-seams.md) | No legal entity yet — test mode + configuration seams | Accepted | Business / config |
| [0016](./0016-global-shipping-dap-tax-at-home.md) | Global = ship worldwide DAP, tax presence at home only | Accepted | Business / tax |
| [0017](./0017-xero-nightly-summary-journals.md) | Xero sync — nightly summary journals + per-refund detail | Accepted | Accounting |
| [0018](./0018-support-tickets-rma-state-machine.md) | Support = order-linked tickets + RMA state machine; no chat/KB | Accepted | Support |
| [0019](./0019-blazor-server-admin.md) | Blazor Server admin app behind the gateway | Accepted | Admin |
| [0020](./0020-postgres-fts-search.md) | Search via Postgres FTS + pg_trgm behind ISearchProvider | Accepted | Catalog / search |
| [0021](./0021-containerized-launch.md) | Containerized launch (compose → Helm/k8s) alongside bare-run dev | Accepted | Deployment / infra |
| [0022](./0022-named-schema-per-service.md) | Named schema per service (within each service database) | Accepted | Data / persistence |
| [0023](./0023-strict-multi-tenancy.md) | Strict multi-tenancy — tenant = legal operator, principals span tenants | Accepted | Multi-tenancy |
| [0024](./0024-tenant-isolation-postgres-rls.md) | Tenant isolation via PostgreSQL RLS with transaction-scoped `SET LOCAL` | Accepted | Multi-tenancy / data |
| [0025](./0025-pdp-pep-dynamic-rbac.md) | Central PDP + service-side PEP, field/action policy, dynamic admin-defined RBAC | Accepted | Security / authz |
| [0026](./0026-service-accounts-and-cli.md) | Service accounts + installable .NET global-tool CLI | Accepted | Security / tooling |
| [0027](./0027-entity-service-master-data-boundary.md) | Entity service master-data boundary | Accepted | Entity / service boundary |
