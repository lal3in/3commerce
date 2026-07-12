# 3commerce — full project analysis

> Text twin of [project-analysis.html](./project-analysis.html). The HTML page is the
> visual-first canonical version (it carries three inline SVG diagrams — system topology,
> checkout/ledger flow, and the audience communication matrix — plus a capability heatmap
> and evidence score bars that do not translate to Markdown). This twin keeps the same
> prose, tables, and findings so the analysis is readable and grep-able in plain text.
> Complete re-analysis refreshed 2026-07-05.

An evidence-led, strategic, and visual audit of the current 3commerce solution: product
scope, domain model, service topology, user journeys, money flow, supplier/tenant/admin
operations, marketing, compliance primitives, deployment, tests, launch gates, strengths,
risks, and communication strategy.

**At a glance:** 13 DB-owning services · 18 runnable app images · 3 user-facing apps ·
1 YARP public origin · current engineering grade **A** · dev uses a fake payment provider
and a logging Xero client. 81+ integration tests noted; 0 known money-core gaps.

## Executive summary

3commerce is no longer just an MVP e-commerce build. It is a governed commerce operating
platform: storefront, admin, supplier portal, CLI, gateway, service-owned databases,
RabbitMQ operational messaging, optional Kafka durable event stream, double-entry ledger,
supplier/entity model, carrier/shipping, inventory, dropship, digital entitlement,
subscriptions, usage, marketing attribution, audit primitives, workflow scheduling,
observability, and deployment automation.

The standout strength is still the **money and operational integrity core**: the ledger is
append-only and balanced by database constraints; refunds flow through one idempotent path;
checkout/RMA are saga-driven; dev fakes reuse production processing seams instead of
bypassing them. The system is complex, but it is complex because the project deliberately
chose distributed-systems learning and production-grade seams over a minimal monolith.

**Verdict:** strong engineering core, broad product surface, excellent
documentation/testing discipline, and an honest path to production. The platform is
strategically credible, but should be sold as *launch-ready after external gates*, not as
already-live SaaS. Live Stripe/Xero, real tax/accounting review, supplier contracts,
external security review, managed cloud deployment, accessibility review, and legal
documents remain business launch gates.

## Graphic 1 — System topology in one picture

*(SVG in the HTML twin.)* Users (Shoppers, Operators, Suppliers, Automation) reach four
apps — Next.js Storefront (SSR/ISR, Server Actions), Blazor Admin (operator control plane),
Supplier Portal (readiness, requests), and the .NET CLI (gateway-only scope). All four go
through the single public **YARP Gateway** origin, which turns the opaque session into
minted claims. Behind the gateway sit the service-owned domains — Identity, Catalog,
Ordering, Payments, Fulfillment, Support, Entity, Marketing, Pricing, Audit, Workflow,
Entitlement, Usage — each with its own Postgres database, RabbitMQ + MassTransit for
operational messaging, and an optional Kafka durable stream lane.

## Capability map

The project now spans seven major capability bands. Some are mature and user-facing; some
are capability-first with dedicated services or future UI still emerging. The important
communication point is to separate **engine exists**, **operator surface exists**, and
**production integration is live**.

| Band | Maturity | Coverage |
|------|----------|----------|
| Storefront | Strong | SSR catalog, product detail, variants, cart, shipping quote, checkout, account, support. |
| Admin | Strong | Orders, RMAs, ledger, imports, catalog, offers, RBAC, entities, payment accounts, payouts, Xero mappings, mission control. |
| Money | Strong | Ledger, payment rail seam, idempotency, refunds, saved cards, subscriptions, overage charge path. |
| Supply | Strong | Offers, inventory owner, reservations, movements, carrier quotes, dropship supplier orders. |
| Digital | Medium | Entitlements/subscriptions/usage backend and customer APIs; storefront affordances still partly deferred. |
| Marketing | Medium | Campaigns, short links, analytics, consent, themes, SEO, feeds; some endpoints/projections still capability-first. |
| Security/RBAC | Strong | Gateway claims, PDP/PEP, dynamic RBAC, dev-secret gate, MFA policy, step-up model. |
| Compliance | Medium | Audit chains, retention/redaction primitives, export/download signing; policy docs and external review still needed. |
| Deployment | Strong | Bare-run, compose, Helm/kind, migrator, Dockerfiles, optional PgBouncer/Kafka/observability. |
| Testing | Strong | Unit, Testcontainers integration, Playwright E2E, e2e-verify, CI jobs. |
| Live rails | Medium | Stripe adapter exists; Xero logging dev client; production OAuth/tax/provider onboarding are launch gates. |
| Commercial readiness | Cold | Terms, privacy, supplier contracts, SLAs, accessibility review, security review still external gates. |

## Service-by-service analysis

| Service | Current ownership | Strength | Open caveat |
|---------|-------------------|----------|-------------|
| Identity | Sessions, users, tenants/principals, RBAC, PDP, MFA policy. | Opaque session + gateway claims model; dynamic permissions. | MFA policy exists; production enrollment/support process needs final ops policy. |
| Catalog | Products, categories, variants, storefronts, publications, offers, search. | FTS + trigram search; offer-based supply model; admin CRUD. | Catalog scale depends on representative production data/index audits. |
| Ordering | Cart, checkout attempts, orders, order status, offer snapshots. | Variant/currency-aware checkout, saga choreography, selected shipping/tax snapshots. | Pre-payment reservation hold is optional refinement for oversell window. |
| Payments | Ledger, payment accounts, refunds, saved cards, subscriptions, payouts, Xero mappings. | Append-only balanced ledger; single refund path; provider seams. | Renewal/overage ledger journaling is noted refinement; live Xero/Stripe gated. |
| Fulfillment | Inventory, reservations, movement ledger, carriers, quotes, shipments, dropship, entitlements/usage legacy homes. | Single stock owner and carrier fallback keep checkout moving. | Real carrier labels/tracking require credentialed adapters and operational testing. |
| Support | Tickets and RMA saga. | Server-derived RMA amount and refund saga reuse. | RMA saga remains mostly whole-order; restock granularity covers partial return stock. |
| Entity | Parties, identifiers, addresses, supplier onboarding/change requests. | Master-data foundation with maker-checker and RLS middleware progress. | RLS rollout for all Entity child tables remains a follow-up per tracker notes. |
| Marketing | Campaigns, short links, analytics events, content publishing, product feeds. | Safe-by-construction sanitization and consent-aware storefront tracking. | Some persistence/projection/feed endpoints are capability-first or incremental. |
| Pricing | Dedicated prices + graduated tiers per API index. | Separates advanced pricing from catalog offers. | Earlier plan notes show capability-first evolution; keep API/docs/code parity under watch. |
| Audit | Central searchable projection over local hash-chained audit facts. | Local source-of-truth audit chains avoid central-log trust fallacy. | Audit coverage rollout across all sensitive surfaces is incremental. |
| Workflow | Scheduled job run history and scheduling policy. | Typed jobs, run records, persistent scheduler path. | Misfire/retry tuning and dashboard depth are production hardening items. |
| Entitlement | Digital/service-line access on confirmed orders. | Digital lines do not create physical shipments. | Storefront my-access UX is not as mature as backend surfaces. |
| Usage | Metered usage balances and overage billing events. | Incremental balances; idempotent recording; overage gate. | Period reset/auto billing and accounting journals need production-grade closing flow. |

## Core flow analysis

**Graphic 2 — Checkout / payment / ledger / fulfilment flow** *(SVG in the HTML twin.)*
Cart → CheckoutAttempt (price · tax · ship) → Payment rail (Stripe/Fake seam) → on success
fans out to Order confirmed (event) and a balanced, append-only Ledger entry, then to
Inventory / Dropship / Entitlement and finally the Customer (status/support).

- **Checkout:** cart lines resolve offers, selected shipping, tax regime, discounts,
  payment option, storefront/tenant/campaign context, and order totals before payment
  success creates the final order.
- **Payment:** Stripe is abstracted behind `IPaymentProvider`; fake dev payment uses the
  same processing path and inbox semantics as a real webhook.
- **Ledger:** confirmed sales and refunds post balanced entries; mutable rail state is not
  treated as financial truth.
- **Fulfilment:** warehouse lines consume inventory; dropship lines forward supplier
  orders; digital/service lines issue entitlements or usage/subscription artifacts.

## Evidence scorecard

The score below is not a claim of commercial certification. It reflects current repository
evidence: docs, plan tracker, API contracts, wiki pages, tests, and implementation
patterns.

| Dimension | Evidence score |
|-----------|----------------|
| Money integrity | 96% |
| Auth/RBAC | 90% |
| Commerce UX | 84% |
| Supplier ops | 80% |
| Digital supply | 72% |
| Compliance docs | 68% |
| Live production | 52% |

## Build history and strategic evolution

- **Phases 1–4** — MVP spine: services, gateway, identity, catalog/search, cart/checkout,
  ledger, Stripe seam, fulfilment, support/RMA, Xero logging, admin, E2E tests.
- **Backlog BL-1..11** — Closed conformance gaps: guest-to-account, admin catalog CRUD,
  order/account screens, server-derived RMA, app Dockerfiles, dev-secret gate.
- **Container launch** — Full compose stack, migrator, Helm chart, CI kind validation,
  optional PgBouncer and observability profiles.
- **MT phases 1–4** — Tenant foundation, PDP/PEP, Entity/Supplier Portal, storefront
  lifecycle, offers, pricing, payments, shipping, inventory, carriers, dropship.
- **MT phase 5** — Marketing, short links, attribution, analytics, consent, themes,
  publishing, SEO metadata, product feeds.
- **MT phase 6** — Audit, workflow, approvals, notifications, webhooks, export/redaction,
  object storage, MFA policy, region/retention, observability, mission control.
- **MT phase 7** — Digital supply, offer price models, entitlements, subscription billing,
  usage metering, overage billing, customer access APIs.
- **Messaging roadmap** — RabbitMQ operational lane plus optional Kafka durable stream
  lane, stream outbox, replay, privacy guardrails, persistent Quartz scheduler.

## Strengths

- **Ledger-first money architecture.** This is the most production-grade part of the
  project: double-entry, append-only, balance enforced at the database, refund reversals,
  and tests around money invariants.
- **Correct seam discipline.** Payment, tax, carriers, Xero, supplier orders, object
  storage, notifications, webhooks, and streaming are seams. Dev fakes exist, but they are
  not shortcuts around core flows.
- **Real operational control plane.** Admin is not a toy screen: catalog, offers, orders,
  RMA, ledger, Xero, payment accounts, supplier payouts, entities, RBAC, mission control,
  and commerce ops are represented.
- **Supplier and tenant model is strategic.** Entity master data, supplier onboarding,
  change requests, payout tokens, tenant/storefront lifecycle, domains, RBAC, and PDP/PEP
  point toward a real multi-tenant operating business model.
- **Testing and documentation are unusually strong.** Unit tests, Testcontainers
  integration, Playwright, e2e-verify, CI, ADRs, API contracts, runbooks, and this wiki
  create a rare evidence trail.

## Risks and gaps

| Risk | Why it matters | Mitigation / status |
|------|----------------|---------------------|
| Operational complexity | 13 services, 18 app images, RabbitMQ, optional Kafka, Postgres DBs, gateway, frontends. | Compose/Helm/migrator scripts reduce friction; still needs production SRE discipline. |
| Live payment/accounting/tax not complete | Cannot claim production financial compliance without live rails and review. | Provider seams exist; legal entity, Stripe/Xero OAuth, tax strategy, accounting review remain launch gates. |
| Compliance claims | Risk of over-selling privacy, PCI, tax, security, or accessibility posture. | Use careful language from Selling information; complete policies and external review before launch. |
| Capability-first surfaces | Some services/features exist as primitives or APIs before full polished UI/ops. | Track separately in roadmap; avoid presenting all as equally mature. |
| RLS/tenant isolation rollout unevenness | Tracker notes child-table RLS follow-ups in Entity and some app-level tenant filtering elsewhere. | Tenant middleware and RLS tests exist; expand non-superuser tests before production. |
| Scheduler/stream operational hardening | Persistent scheduler and Kafka lane add operational responsibility. | Runbooks and optional profiles exist; production broker/security choices still needed. |
| Frontend/tooling split | Next.js + Blazor + CLI gives audience fit but doubles maintenance. | Acceptable for learning; commercial team needs ownership boundaries. |

## Strategic communication strategy

The safest and strongest positioning is: **operator-grade commerce platform with
transparent launch gates**. Avoid implying that every integration is live or certified.
Sell the architecture, operational control, and correctness; disclose fake/sandbox/dev
rails and external gates.

**Graphic 3 — Who cares about what** *(SVG in the HTML twin.)* 3commerce sits at the centre
as a governed commerce spine (not just a storefront), connecting Shoppers, Tenants, and
Suppliers on one side to Admins, Tech buyers, and Compliance reviewers on the other.

- **For customers:** emphasize clarity, account support, refund path, shipping selection,
  and saved details.
- **For tenants:** emphasize storefront lifecycle, offer/supply model, payment readiness,
  RBAC, tax/currency configuration, and launch gates.
- **For suppliers:** emphasize onboarding, readiness, availability feeds, dropship
  forwarding, and change-request approval.
- **For admins:** emphasize operational control, traceability, idempotency, ledger, and
  mission control.
- **For technical buyers:** emphasize service ownership, outbox, sagas, real tests, API
  contracts, deployability, and documented trade-offs.
- **For compliance reviewers:** use cautious wording: reduced PCI scope, tax seam, ASVS
  self-audit, audit primitives, retention/redaction primitives — not certifications.

## Launch gates

| Gate | Blocks | Current status |
|------|--------|----------------|
| Legal entity | Live Stripe/Xero, merchant terms, tax registration, privacy/imprint, payout currency. | External business gate. |
| Payment provider setup | Real card payments, webhook secret rotation, PCI responsibility matrix. | Stripe adapter exists; dev uses FakePaymentProvider. |
| Accounting setup | Real Xero OAuth org and chart mapping review. | Xero mapping exists; dev uses LoggingXeroClient. |
| Tax strategy | Jurisdiction-specific inclusive/exclusive handling and reporting. | Tax seams and storefront config exist; formal tax review needed. |
| Supplier contracts | Real catalog feeds, dropship/warehouse decisions, carrier credentials. | Supplier/dropship/carrier models exist; contracts/credentials pending. |
| Security review | External validation before production handling of real customer/payment-adjacent flows. | ASVS self-audit and tests exist; external pen test pending. |
| Managed production platform | Cloud cluster, secrets manager, backup/restore, monitoring, incident process. | Compose/Helm/kind proven; managed target pending. |
| Legal/commercial docs | Terms, privacy, cookies/consent, returns, supplier terms, SLA/AUP. | Not code; required before selling as live service. |

## Final verdict

**3commerce is a serious, unusually well-documented distributed commerce platform with a
production-grade money core and a broad operating model.** It is not a simple shop demo; it
is a commerce spine with explicit domains for identity, catalog, ordering, payments,
fulfilment, support, entity/supplier operations, marketing, pricing, audit, workflow,
entitlement, and usage.

**The best use of this analysis:** communicate confidently about the architecture,
workflows, and proof points, while being precise about maturity. The platform is strong
enough to demo strategically and evaluate technically; production selling requires
completing external legal, tax, payment, accounting, security, accessibility, supplier, and
cloud gates.

**One-line assessment:** high-complexity by design, high-integrity where it matters, broad
in scope, honest about launch gates, and credible as both a distributed-systems learning
reference and the foundation for an operator-grade commerce business.

> Evidence used: current help wiki, API contracts index, plan execution tracker through
> phases 1–7, container/platform/messaging roadmaps, and documented launch gates. Companion
> page for buyer-safe phrasing: [Selling information](./selling-information.md).
