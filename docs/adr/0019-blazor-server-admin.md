# ADR-0019: Blazor Server admin app behind the gateway

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #19, `docs/prd/3commerce/15-appendix.md`)

## Context

The operator needs catalog CRUD, order inspection, import monitoring, an RMA queue, and refund issuing. Internal tooling has different constraints than the storefront: few users, no SEO, security over polish.

## Decision

A separate **Blazor Server** application: all-C#, shares DTO contracts with services, stateful server model (fine for a handful of staff). Access requires the `admin` role claim from Identity **plus** network controls: separate subdomain and IP allowlist. Blazor's storefront weaknesses (SEO, first load) are irrelevant here.

## Alternatives considered

- **`/admin` routes in Next.js** — rejected: admin logic in TypeScript; a storefront XSS is one hop from admin actions.
- **Retool/AdminJS-style tooling** — rejected: RMA approval and refunds are saga-triggering actions, not table edits.
- **No UI (curl/scripts)** — rejected: a real business can't run on it past week one.

## Consequences

- Two frontend frameworks exist (with Next.js — PRD risk R-5); admin deliberately stays plain CRUD-grade UI.
- Admin actions go through the gateway/API like any client — no privileged DB access.
