# 3commerce

A from-scratch, strictly multi-tenant e-commerce platform for physical goods sourced from large
third-party catalogs — built as C# microservices and deliberately dual-purpose: a launchable real
business **and** a hands-on distributed-systems learning vehicle (ADR-0001).

## What it is

- **13 services** (Identity, Catalog, Entity, Ordering, Payments, Fulfillment, Support, Marketing,
  Pricing, Audit, Workflow, Entitlement, Usage), async-first over RabbitMQ/MassTransit with EF
  transactional outbox; one PostgreSQL database per service, named schema, tenant isolation via
  RLS (ADR-0022/0023/0024).
- **YARP gateway** as the single public origin: opaque session cookie at the edge → short-lived
  signed internal-claims JWT to services (ADR-0011/0012). MFA (TOTP) with tenant policy.
- **Money**: append-only double-entry ledger as source of truth; payment providers behind a keyed
  registry with fail-closed LocalMock/Sandbox/Production modes (ADR-0014/0039); nightly Xero
  summary journals (ADR-0017).
- **Commerce**: multi-storefront tenants with per-storefront currency + tax regime and tenant-set
  per-currency shelf prices (ADR-0038); composable supply via Offers — warehouse, dropship,
  digital/subscription/usage lines (ADR-0028); checkout saga, shipping quotes + carriers,
  RMA/refund saga (single refund path).
- **Frontends**: Next.js SSR storefront (`src/Storefront`), Blazor Server admin (`src/Admin`),
  supplier portal (`src/SupplierPortal`).

## Quickstart (local dev, ADR-0009)

```bash
# infra (Postgres + RabbitMQ in Docker) → migrations → all services as host processes
scripts/dev-up.sh --fresh --with-frontends --seed
# storefront :3000 · admin :5200 · gateway :8080 · services :5101-:5113
scripts/doctor.sh        # one-shot health triage
scripts/dev-down.sh      # stop everything
```

Containerized launch (full stack): `scripts/launch.sh [--fresh|--reuse] [--env dev|prod]`
(ADR-0021); a Helm chart lives under `deploy/helm/3commerce`. See `scripts/README.md` for the
full script catalog.

## Validation

```bash
dotnet build 3commerce.sln && dotnet format --verify-no-changes
dotnet test 3commerce.sln
scripts/e2e-verify.sh [--live]   # full regression
```

## Where things live

| What | Where |
|---|---|
| Agent/contributor rules, invariants, structure | `AGENTS.md` |
| Architecture decisions | `docs/adr/adr_index.md` |
| API contracts (OpenAPI per service) | `docs/api/api_contracts_index.md` |
| Event-stream + messaging contracts | `docs/api/event-streams.md`, `docs/api/message-scheduling.md` |
| PRD | `docs/prd/3commerce/` |
| Conformance review / security audit | `docs/reviews/prd-vs-implementation.md`, `docs/security/asvs-l1-audit.md` |
| Operator wiki (HTML) | `docs/help/index.html` |
| Execution status tracker | `.ai-shared/plans/plan_status_executions.md` |

**Status**: MVP + multi-tenant platform expansion complete on dev/test rails. Remaining launch
gates are non-code: business registration → live Stripe/Xero + carrier credentials, external pen
test, managed cluster (`docs/prd/3commerce/15-appendix.md`, Appendix B).
