# 3commerce Help Wiki

This wiki documents how to **run and operate the 3commerce frontends** — the
customer-facing **Storefront**, internal **Admin** console, Supplier Portal,
CLI, and the gateway-backed services — step by step and accurate to the code.

## What the apps are

| App | Tech | Port | Talks to | Audience |
|-----|------|------|----------|----------|
| **Storefront** | Next.js 15 (App Router, SSR/ISR) + React 19 + Tailwind | `:3000` | YARP gateway only (`:8080`) | Customers (browse, buy as guest, account, support) |
| **Admin** | Blazor Server (.NET 10) | `:5200` | YARP gateway only (`:8080`), `admin` role required | Operators (orders, RMAs, ledger, Xero, imports, **entities & suppliers, roles & permissions, commerce ops**) |
| **Supplier Portal** | Blazor Server (.NET 10) | `:5300` | YARP gateway only (`:8080`), supplier session | Suppliers (readiness, stock feeds, change requests) |
| **Gateway** | YARP | `:8080` | The 13 DB-owning backend services | Single public origin; session validation, internal claims |
| **CLI** | .NET global tool (`src/Cli`) | — | YARP gateway only | Operators/automation (Gateway-only, explicit `--tenant` scope) |

Both frontends are thin: they **only** reach the backend through the gateway and
hold no database access. The Storefront mutates state through **Server Actions**
(`src/Storefront/lib/*-actions.ts`); the Admin console calls the gateway through
`GatewayClient`. Authentication is an opaque `3c_session` cookie issued by the
Identity service via the gateway.

> Backend, for context: 13 DB-owning C# services — Identity, Catalog, Ordering,
> Payments, Fulfillment, Support, Entity, Marketing, Pricing, Audit, Workflow,
> Entitlement, and Usage — plus the gateway and a Notifications worker. Services
> communicate over RabbitMQ/MassTransit and own separate Postgres databases/schemas.
> In dev there is **no live Stripe or Xero** — a `FakePaymentProvider` and a
> `LoggingXeroClient` stand in.

> **Platform expansion.** The codebase now includes the multi-tenant platform
> foundation (a tenant = one legal business; PostgreSQL row-level security + central
> PDP/PEP with dynamic admin-defined RBAC), the **Entity** master-data service,
> Supplier Portal, CLI, and the six ADR-0030 services (Marketing, Pricing, Audit,
> Workflow, Entitlement, Usage). **Composable supply** (ADR-0028) sells products via
> **Offers** `(product/variant × supplier) → supply type + price`; Fulfillment owns
> inventory/reservations, movement ledger, carrier quotes (Fake/AusPost/DHL sandbox),
> selected checkout shipping rates, and dropship supplier-order forwarding. Operator UI
> covers offers/pricing, payment accounts, supplier payouts, and Xero mappings; API
> surfaces remain under `/api/fulfillment/*`, `/api/catalog/admin/offers`, and
> `/api/payments/admin/*`. See ADRs `0023`–`0030` and the phase plans under
> `.ai-shared/plans/`.

## Table of contents

| Page | What it covers |
|------|----------------|
| [Getting started](./getting-started.md) | Prerequisites and the exact step-by-step to bring the whole stack up locally (infra → migrations → services → storefront → admin). |
| [Storefront operations](./storefront-operations.md) | Every storefront page and flow from the user's perspective: browse, search (typo-tolerant), product detail, cart, guest checkout → payment → confirmation, register/login/logout, account, support tickets, refund (RMA). |
| [Admin operations](./admin-operations.md) | Admin login and every admin screen, including the operator **RMA approve → refund** flow, plus ledger, Xero sync, and imports. |
| [Testing](./testing.md) | Running unit, integration (Testcontainers), and Playwright E2E (storefront + admin), the `scripts/e2e-verify.sh [--live]` regression script, and how CI runs everything. |
| [Deployment](./deployment.md) | Current local-only deployment, Dockerfiles, configuration/secrets, and the honest state of production / k8s and the launch gates. |
| [Platform services](./services.md) | The six new DB-owning services (Marketing, Pricing, Audit, Workflow, Entitlement, Usage) — what each owns, every endpoint, and use cases per option. |
| [Users, roles & permissions](./roles-permissions.md) | Principal types, the gateway→claims→PDP/PEP pipeline, the six built-in roles, the 24 code-defined permissions with risk, and dynamic admin RBAC. |
| [UI screens](./screens.md) | Live Playwright screenshots of every storefront + admin screen with its buttons and use cases. |
| **Project analysis** | [project-analysis.html](./project-analysis.html) — detailed assessment: architecture, tech, best/worst, strengths vs drawbacks, trade-off comparisons, and the verdict (open in a browser). |

## Conventions used in this wiki

- **Money** is always integer **minor units** (cents) + an ISO 4217 currency code.
  The Storefront formats it through `src/Storefront/lib/money.ts`; never divide by
  100 in a page.
- **URLs/ports** are the local dev defaults: gateway `:8080`, storefront `:3000`,
  admin `:5200`, supplier portal `:5300`, backend services `:5101`–`:5113`.
- File paths are relative to the repo root
  (`/Users/lehn/Documents/Git-Roots/3commerce`).
