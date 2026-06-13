# Frontend Component Guidelines

Standards for building UI components in the **Next.js (App Router, SSR) storefront** and, where marked, the Blazor admin. Grounded in ADR-0010 (Next.js storefront), ADR-0012 (cookie auth), and PRD UX goals (§11). Read this before creating or modifying frontend components.

---

## 1. Server-first component model

- **Server Components are the default.** Add `'use client'` only when the component needs state, effects, event handlers, or browser APIs — and push that boundary to the **leaf** of the tree. A `'use client'` high in a subtree silently promotes everything under it to client and forfeits the RSC bundle-size win (40–60% on storefronts).
- Fetch data **on the server** in Server Components / route segment code, and pass it down as props. Client components receive data; they do not fetch it (exception: genuinely interactive reads like search-as-you-type, which go through a route handler).
- Never read cookies/headers in layouts — it forces the whole segment dynamic. Read them only in the leaf segments that truly need them (cart badge, account area).

### Rendering strategy per page type

| Page | Strategy | Why |
|---|---|---|
| Home, category, product detail | Static + ISR (revalidate on product events / time-based) | SEO + speed at thousands of SKUs |
| Search results | Dynamic SSR, URL-driven | Query params are the state |
| Cart, checkout, account, orders | Dynamic (cookie-dependent), no caching | Personalized, never shared |

## 2. Talking to the backend

- The browser and server components talk **only to the YARP gateway** — never to a service directly (ADR-0011).
- The session cookie is the auth credential. Server-side fetches forward it explicitly; no tokens in `localStorage`, ever (ADR-0012).
- **Mutations go through Server Actions** (or route handlers) that call the gateway server-side: cookies and internal URLs stay off the client, and forms degrade gracefully. One action per use case (`addToCart`, `submitCheckout`), colocated with the feature, returning typed state for `useActionState`.
- Saga-backed mutations (checkout) are **pending-first**: render the returned saga state (`PaymentRequested`…), then confirm via redirect/poll — never pretend synchronous success (ADR-0007).

## 3. Component organization

```
src/Storefront/
├── app/                    # routes: route folders kebab-case; page/layout/loading/error per segment
│   └── (shop)/products/[slug]/page.tsx
├── components/
│   ├── ui/                 # shadcn/ui primitives ONLY — generated, pure, no business logic
│   └── <feature>/          # cart/, catalog/, checkout/, account/ — composed feature components
└── lib/                    # gateway client, money/format helpers, server action helpers
```

- `components/ui/` stays **pure and reusable** (shadcn convention: you own the source, but don't entangle it with domain logic). Business behavior lives in `components/<feature>/`.
- Components PascalCase; one exported component per file; props typed with an explicit `interface XProps`, no `any`.
- **Composition over configuration**: prefer `<ProductCard><ProductCard.Badge/></ProductCard>` slots and `children` over boolean-prop explosions. Style variants via `cva` (class-variance-authority), not prop-driven `className` string building.

## 4. State management

- **URL is the state** for anything shareable/SEO-relevant: search query, filters, pagination, sort live in search params — not in client state. This makes filtered catalog views linkable and crawlable.
- Cart state lives **in the Ordering service** (cookie-keyed) — the client renders what the server returns; optimistic UI via `useOptimistic` is allowed for add/remove, reconciled against the server response.
- No global state library (Redux/Zustand) until a concrete need is demonstrated — server components + URL + small leaf state cover this app's needs.

## 5. UX, accessibility, performance (PRD §11 goals are binding)

- **Accessibility:** build on the shadcn/Radix primitives (ARIA, focus management, keyboard nav come included) — don't reimplement dialogs/menus/popovers by hand. Manually verify tab order and focus traps in dialogs and forms; every image gets meaningful `alt`; form fields get visible labels and error text tied via `aria-describedby`.
- **Loading:** every async segment has `loading.tsx` with **skeletons matching final layout** — zero layout shift on product grids is a stated UX goal. Use Suspense streaming for below-the-fold sections (reviews-later, related products).
- **Errors/empty:** every segment has `error.tsx` and a designed empty state (empty cart, no search results). Errors render ProblemDetails messages from the gateway, never raw stack traces.
- **Images:** `next/image` always (remote patterns configured for catalog image hosts); explicit width/height or `fill` to prevent shift.
- **Money:** values arrive as integer minor units + currency code (AGENTS.md invariant). Format exclusively via the shared `formatMoney(minorUnits, currency)` helper in `lib/` — never divide by 100 inline, never hardcode a currency symbol (currency is config, ADR-0015).
- **Tailwind:** utilities in JSX are the styling layer — no CSS modules/styled-components. Shared design tokens via CSS variables per shadcn theming; if a class string needs reuse, extract a component, not an `@apply` class.

## 6. Blazor admin (differences only)

- Same gateway, same auth model, same money/ProblemDetails rules.
- Plain CRUD-grade UI is acceptable (ADR-0019) — no design-system ambitions; prefer QuickGrid/simple forms; saga-triggering buttons (approve RMA, refund) must disable on click and render the returned state (idempotent server, but don't invite double-clicks).

## Definition of done for a component

- [ ] Server Component unless interactivity demands otherwise; `'use client'` at the leaf
- [ ] Data via props from server fetch through the gateway; no client-side secrets
- [ ] Typed props; variants via `cva`; no `any`
- [ ] Keyboard + focus behavior verified; labels/alt text present
- [ ] Skeleton/loading, error, and empty states exist; no layout shift
- [ ] Money through `formatMoney`; URL holds shareable state
- [ ] `npm run lint && npx tsc --noEmit` clean

---

## Sources

- [Next.js — Server and Client Components](https://nextjs.org/docs/app/getting-started/server-and-client-components)
- [React Server Components in practice — App Router patterns, streaming, caching](https://medium.com/@vyakymenko/react-server-components-in-practice-next-js-d1c3c8a4971f)
- [Next.js App Router Patterns in 2026](https://pristren.com/blog/nextjs-app-router-patterns-2026/)
- [Building an E-Commerce Store with Next.js — Full Guide](https://www.ksolves.com/blog/next-js/building-an-e-commerce-store)
- [The Ultimate shadcn/ui Handbook (2026)](https://shadcnspace.com/blog/shadcn-ui-handbook)
- [Infinum Frontend Handbook — React/Tailwind/shadcn](https://infinum.com/handbook/frontend/react/tailwind/shadcn)
- [Accessible shadcn/ui components guide](http://www.blog.brightcoding.dev/2025/12/15/the-ultimate-guide-to-accessible-shadcn-ui-components-build-production-ready-apps-with-react-typescript-tailwind-css)
- [shadcn/ui — Tailwind v4](https://ui.shadcn.com/docs/tailwind-v4)
