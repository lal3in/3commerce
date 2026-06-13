# 3. Target Users

## Primary personas

### 1. The Shopper (customer)
- **Who:** general consumers buying physical goods online; arrive via search/ads/social.
- **Technical comfort:** low — expects the polish of mainstream stores (fast pages, instant search, Apple/Google Pay).
- **Key needs:**
  - Find the right product quickly in a large catalog (search with typo tolerance, attribute filters).
  - Buy **without being forced to create an account** (guest checkout is mandatory — forced registration is a top documented cause of cart abandonment).
  - Track orders, see history, and request refunds without emailing anyone.
- **Pain points addressed:** opaque order status, refund black holes, account-wall friction.

### 2. The Operator / Admin (initially the owner)
- **Who:** runs the store day-to-day; approves RMAs, issues refunds, curates the catalog, monitors supplier feed sync.
- **Technical comfort:** high (it's the builder, initially), but the admin UI must be usable by a future non-technical hire.
- **Key needs:**
  - One screen per order showing payment state, fulfillment source per line item, and ticket history.
  - RMA queue with approve/deny and one-click refund that handles Stripe + ledger + Xero automatically.
  - Catalog management and supplier import monitoring (row counts, validation errors, orphaned products).
- **Pain points addressed:** manual bookkeeping (Xero sync), refund processes spanning three systems, supplier data chaos.

### 3. The Builder (solo developer — meta-persona)
- **Who:** experienced C# developer learning distributed systems by building them.
- **Technical comfort:** expert in C#/.NET; learning MassTransit, sagas, RabbitMQ operations, Next.js.
- **Key needs:**
  - Architecture that genuinely exercises distributed patterns (not a distributed monolith).
  - A fast inner dev loop: `dotnet run` per service, only Postgres + RabbitMQ in containers.
  - Code that remains launchable as a real business — learning artifacts and business artifacts are the same artifacts.
- **Pain points addressed:** tutorials that don't survive contact with production; platforms (Shopify) that hide everything worth learning.

## Explicit non-users (v1)

- Third-party sellers (this is a store, not a marketplace).
- Support agents/teams (no agent assignment, SLAs, or multi-agent helpdesk — one operator).
- B2B/wholesale buyers (no quote flows, no VAT-ID invoicing).
