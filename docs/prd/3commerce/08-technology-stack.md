# 8. Technology Stack

## Backend

| Technology | Choice | Justification |
|---|---|---|
| Runtime | **.NET 10 (LTS)** / C# | Owner's stack; LTS for a long-lived codebase |
| Web framework | ASP.NET Core (minimal APIs per service) | One small API surface per service |
| ORM | **EF Core 10** + **Npgsql** | Migrations per service; MassTransit outbox integrates with EF |
| Database | **PostgreSQL 17** — one container, one database per service | Hard service isolation with single-container ops; FTS + pg_trgm + JSONB cover search and attributes without extra engines |
| Messaging | **RabbitMQ** + **MassTransit v8** | Canonical .NET stack: saga state machines, EF transactional outbox, retries, scheduling — the distributed patterns are the learning goal |
| Gateway | **YARP** | Reverse proxy as code in C#; custom session-validation middleware possible |
| Password hashing | **Argon2id** via vetted library (e.g. `Konscious.Security.Cryptography` or libsodium binding) | Custom auth *flows*, never custom crypto |
| Payments | **Stripe.net** (Payment Intents, test mode) | Only v1 adapter behind `IPaymentProvider` |
| Accounting | **Xero API** (OAuth2; official .NET SDK or minimal client) | Nightly journals + per-refund postings |
| Observability | **OpenTelemetry** (.NET SDK; MassTransit + ASP.NET Core instrumentation) | One trace per checkout across all services; export to local Jaeger/Grafana stack optional |
| Testing | **xUnit**, **Testcontainers for .NET**, MassTransit test harness | Integration tests against real Postgres/RabbitMQ; ledger invariant property tests |

## Frontend

| Technology | Choice | Justification |
|---|---|---|
| Storefront | **Next.js (App Router, SSR)** + TypeScript | SEO for thousands of product pages; best UI ecosystem for "great graphic UX" |
| Styling/UI | Tailwind CSS + shadcn/ui; Framer Motion where it earns its keep | Polished consumer UX with low custom-CSS burden |
| Payments UI | Stripe Payment Element | Card data never touches our servers (SAQ-A scope) |
| Admin | **Blazor Server** (.NET 10) | All-C# internal tooling; stateful model fine for few staff users; its weaknesses (SEO, first load) don't matter internally |

## Infrastructure (v1)

| Concern | Choice |
|---|---|
| Local infra | `docker-compose.infra.yml`: Postgres 17, RabbitMQ (management UI enabled) |
| Local services | Bare `dotnet run` per service (fast inner loop, full debugger); Next.js dev server |
| Containerization | Dockerfile per service, build-verified in CI — deploy story deferred |
| Orchestration | ❌ none in v1; Kubernetes is the post-MVP deployment-learning phase |
| Secrets (local) | .NET user-secrets + `.envrc` (direnv); never committed |

## Pinned conventions

- One solution (`3commerce.sln`); each service = `Api` + `Domain` + `Infrastructure` projects + test project.
- Shared `BuildingBlocks.Contracts` package holds **message contracts only** (records, versioned additively); `BuildingBlocks.Infrastructure` holds outbox/OTel/auth plumbing. No shared domain logic.
- All money represented as `long` minor units + ISO currency code (no `decimal` arithmetic on money paths without explicit rounding rules; never `double`).
- IDs: UUIDv7 primary keys (time-ordered, index-friendly in Postgres).

## Third-party services summary

| Service | Mode | Notes |
|---|---|---|
| Stripe | Test mode only until legal entity exists | Live keys are a launch task |
| Xero | Demo company org | Real org requires registration |
| Email provider | Sandbox/dev domain | Behind `IEmailSender`; provider choice deferred, any transactional SMTP/API provider fits |
