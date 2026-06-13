# ADR-0010: Next.js (SSR) storefront

- **Status:** Accepted
- **Date:** 2026-06-12
- **Source:** PRD design interview (decision log #10, `docs/prd/3commerce/15-appendix.md`)

## Context

"Great graphic UX" is an explicit requirement, and thousands of product pages need real SEO — client-only rendering underperforms for organic search at catalog scale.

## Decision

The storefront is **Next.js (App Router) with server-side rendering**, TypeScript, Tailwind CSS + shadcn/ui. Cost accepted: a second language (TypeScript) alongside C#, since backend is where the learning focus lies.

## Alternatives considered

- **SPA (Vite/React) without SSR** — rejected: weak SEO for a large catalog.
- **Blazor WASM** — rejected for the storefront: thin consumer-UX ecosystem, heavy first load, SEO workarounds (fine for admin — ADR-0019).
- **Razor Pages + htmx** — rejected: rich cart/checkout interactivity and animation would fight it.

## Consequences

- Two-language frontend estate (Next.js + Blazor admin) — tracked as PRD risk R-5.
- Storefront stack stays conventional (no exotic state management) to bound the maintenance tax.
