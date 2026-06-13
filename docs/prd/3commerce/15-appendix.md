# 15. Appendix

## A. Decision log (from the design interview, 2026-06-12)

| # | Decision | Alternatives rejected | Key reason |
|---|---|---|---|
| 1 | Dual purpose: learning **and** real business | Pure prototype; buy Shopify | Learning value requires building; business requires production quality |
| 2 | Physical goods, large third-party catalog | Small own catalog; digital; services | Owner's business direction |
| 3 | Fulfillment undecided → **per-line-item `FulfillmentSource`** | Committing to dropship or warehouse | Cheap to model now, brutal to retrofit |
| 4 | No supplier yet → neutral schema + `ISupplierImporter` + seeded sample data | Waiting for supplier; scraping (rejected outright) | Build never blocks on business |
| 5 | **Microservices (6 services)** — distributed-systems learning is the point | Modular monolith (recommended); 3 coarse services; 9+ fine services; entity-per-service | Explicit, informed choice; accepted 3–5× cost |
| 6 | Services: Identity, Catalog, Ordering(+cart), Payments(+ledger), Fulfillment, Support | Cart as own service (classic mistake); entity decomposition | Capability seams, low coupling |
| 7 | **MassTransit + RabbitMQ**, async-first, EF outbox, sagas | Kafka+event sourcing; sync REST chains; Dapr | Canonical .NET; teaches outbox/saga/idempotency directly |
| 8 | One Postgres container, **database per service** | Schema-per-service; instance-per-service; shared tables | Hard isolation with one-container ops |
| 9 | Local dev: bare `dotnet run` × N, containers only for infra | .NET Aspire (recommended); full compose; local k8s | Simplest inner loop; k8s deferred to deploy phase |
| 10 | **Next.js SSR storefront** | SPA; Blazor WASM; Razor+htmx | SEO at catalog scale + "great graphic UX" requirement |
| 11 | **YARP gateway**, single origin, auth at edge | Next.js BFF; infra-only gateway; per-service CORS exposure | Stays C#; one auth chokepoint; private services |
| 12 | **Custom auth**: opaque HttpOnly cookie + internal signed claims; Argon2id via library; `IAuthService` seam | Browser JWTs; per-request Identity calls; plain trusted headers; raw crypto (prohibited) | Revocable, XSS-resistant, teaches both stateful & stateless auth |
| 13 | Guest checkout + optional accounts; MFA/social deferred | Forced accounts; full auth suite v1; guest-only | Abandonment data; harden before extending |
| 14 | **Stripe only in v1** behind `IPaymentProvider`; custom **double-entry ledger** as source of truth | Polar as rail (impossible: digital-only MoR, store sells physical goods); dual adapters v1 | One rail until revenue; ledger is the durable learning artifact |
| 15 | No legal entity yet → test mode, config currency, `ITaxStrategy`, DAP shipping | Pretending a jurisdiction | Registration is a launch gate, not a build gate |
| 16 | "Global day one" = ship worldwide DAP, tax presence at home only | Full multi-jurisdiction tax registrations | Lean-global pattern of small stores |
| 17 | **Xero**: nightly summary journals + per-refund detail | Per-order real-time invoices; payout-only; CSV | Accountant-preferred; rate-limit safe (60/min, 5k/day) |
| 18 | Support: order-linked tickets + **RMA state machine**, refund saga; no chat/KB | Mailto-only; external helpdesk; full helpdesk | Integrates every service; bounded scope |
| 19 | **Blazor Server admin** behind gateway, admin role + subdomain/IP allowlist | Admin in Next.js; Retool-style; curl-only | All-C# internal tooling where Blazor's weaknesses don't matter |
| 20 | **Postgres FTS + pg_trgm** behind `ISearchProvider` | Meilisearch/Typesense day one; Elastic; SQL LIKE | No new infra at 10k-SKU scale; swap path preserved |

## B. Open business blockers (launch gates, not build gates)

1. **Company registration** — country unknown → blocks Stripe live keys, Xero production org, real tax strategy, payout currency, privacy policy/imprint.
2. **Supplier contract** — blocks real catalog data and forces the dropship-vs-warehouse decision.

## C. Key external dependencies

| Dependency | Docs |
|---|---|
| MassTransit (sagas, EF outbox) | https://masstransit.io/documentation |
| YARP | https://microsoft.github.io/reverse-proxy/ |
| Stripe Payment Intents / webhooks | https://stripe.com/docs/payments/payment-intents · https://stripe.com/docs/webhooks |
| Xero Accounting API (rate limits, journals) | https://developer.xero.com/documentation/ |
| Npgsql / EF Core PostgreSQL | https://www.npgsql.org/efcore/ |
| Postgres FTS + pg_trgm | https://www.postgresql.org/docs/current/textsearch.html |
| OWASP ASVS / password storage guidance | https://owasp.org/www-project-application-security-verification-standard/ |
| OpenTelemetry .NET | https://opentelemetry.io/docs/languages/dotnet/ |

## D. Related repository artifacts

- `docs/prd/PRD.md` — index (this PRD's hub)
- `.envrc` / direnv — local env var management (already present)
- Repo layout target — see [06-architecture.md](./06-architecture.md) § Repository layout

## E. Glossary

| Term | Meaning here |
|---|---|
| **RMA** | Return Merchandise Authorization — the structured return/refund request flow |
| **Saga** | Long-running multi-service flow coordinated by a MassTransit state machine with compensation |
| **Outbox** | Transactional pattern: DB write and event publish commit atomically, relayed afterward |
| **MoR** | Merchant of Record — platform (e.g. Polar) that legally resells and handles tax; digital goods only |
| **DAP** | Delivered At Place — Incoterm: customer pays import duties; how v1 ships worldwide |
| **SAQ-A** | Lightest PCI DSS scope — card data never touches our servers (Stripe Elements) |
