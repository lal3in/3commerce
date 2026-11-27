# Frontend Component Guidelines

Standards for building UI components in the **Next.js (App Router, SSR) storefront** and, where marked, the Blazor admin. Grounded in ADR-0010 (Next.js storefront), ADR-0012 (cookie auth), and PRD UX goals (§11). Read this before creating or modifying frontend components.

---

## 1. Server-first component model

- **Server Components are the default.** Add `'use client'` only when the component needs state, effects, event handlers, or browser APIs — and push that boundary to the **leaf** of the tree. A `'use client'` high in a subtree silently promotes everything under it to client and forfeits the RSC bundle-size win (40–60% on storefronts).
- Fetch data **on the server** in Server Components / route segment code, and pass it down as props. Client components receive data; they do not fetch it (exception: genuinely interactive reads like search-as-you-type, which go through a route handler).
- Don't add cookie/header reads to layouts — they force the whole segment dynamic. **Exception that already exists:** the root `app/layout.tsx` is dynamic by design — it resolves the storefront from the host/cookie (`lib/storefront-context.ts`, `resolveStorefront`) and the locale through next-intl (`getLocale`), because theme, currency and language are per storefront and per visitor. Nested layouts and new code read cookies/headers only in the leaf segments that truly need them (cart badge, account area).

### Rendering strategy per page type

| Page | Strategy | Why |
|---|---|---|
| Home, category, product detail | Static + ISR (revalidate on product events / time-based) | SEO + speed at thousands of SKUs |
| Search results | Dynamic SSR, URL-driven | Query params are the state |
| Cart, checkout, account, orders | Dynamic (cookie-dependent), no caching | Personalized, never shared |

## 2. Talking to the backend

- The browser and server components talk **only to the YARP gateway** — never to a service directly (ADR-0011).
- The session cookie is the auth credential. Server-side fetches forward it explicitly; no tokens in `localStorage`, ever (ADR-0012).
- **Mutations go through Server Actions** (or route handlers) that call the gateway server-side: cookies and internal URLs stay off the client, and forms degrade gracefully. One action per use case, grouped per feature in `lib/<feature>-actions.ts` (`cart-actions.ts`, `auth-actions.ts`, `subscription-actions.ts`, …), returning typed state for `useActionState`. All gateway calls go through `lib/gateway.ts`.
- Saga-backed mutations (checkout) are **pending-first**: render the returned saga state (`PaymentRequested`…), then confirm via redirect/poll — never pretend synchronous success (ADR-0007).

## 3. Component organization

```
src/Storefront/
├── app/                    # routes: route folders kebab-case (products/[slug], account/…, checkout, search, rum/ relay, …)
├── components/
│   ├── <feature>/          # account/, cart/, catalog/, checkout/, consent/, content/, product/, support/, theme/, telemetry/, i18n/
│   └── *.tsx               # small shared pieces (SafeImage, CountrySelect, LocalTime)
├── lib/                    # gateway client (gateway.ts), money.ts, <feature>-actions.ts server actions, telemetry/
├── messages/ + i18n/       # next-intl translations and request config — user-facing strings go through them
└── e2e*/                   # Playwright suites (storefront, admin, supplier) + e2e-support/ (capture(), fixtures)
```

- There is **no component-library layer** today: no shadcn/ui, Radix or `cva` dependency is installed, and adding one needs a justification (AGENTS.md Boundaries). Shared, domain-free pieces sit at the top of `components/`; business behavior lives in `components/<feature>/`.
- Components PascalCase; one exported component per file; props typed with an explicit `interface XProps` (or a named `type`), no `any`.
- **Composition over configuration**: prefer `children`/slot props over boolean-prop explosions; keep variant class lists in one place in the component rather than building `className` strings from many props.

## 4. State management

- **URL is the state** for anything shareable/SEO-relevant: search query, filters, pagination, sort live in search params — not in client state. This makes filtered catalog views linkable and crawlable.
- Cart state lives **in the Ordering service** (cookie-keyed) — the client renders what the server returns; optimistic UI via `useOptimistic` is allowed for add/remove, reconciled against the server response.
- No global state library (Redux/Zustand) until a concrete need is demonstrated — server components + URL + small leaf state cover this app's needs.

## 5. UX, accessibility, performance (PRD §11 goals are binding)

- **Accessibility:** with no primitives library, every dialog/menu/popover is hand-built and must carry its own ARIA roles (`role="dialog"`, `aria-modal`, labelled-by), focus management and keyboard handling (see `components/consent/ConsentBanner.tsx`). Manually verify tab order and focus traps in dialogs and forms; every image gets meaningful `alt`; form fields get visible labels and error text tied via `aria-describedby`.
- **Loading:** async segments get a `loading.tsx` with **skeletons matching final layout** — zero layout shift on product grids is a stated UX goal. Today only the two pay pages (`account/subscriptions/[id]/pay`, `account/usage-charges/[reference]/pay`) have one; add it when you touch a slow segment. Use Suspense streaming for below-the-fold sections (reviews-later, related products).
- **Errors/empty:** the root `app/error.tsx` + `app/global-error.tsx` catch everything (both render `components/telemetry/ErrorPanel.tsx`, which shows the support reference from `lib/telemetry/error-reference.ts`, ADR-0066); segments with their own failure modes add an `error.tsx` (the pay pages do). Every list has a designed empty state (empty cart, no search results). Errors render ProblemDetails messages from the gateway, never raw stack traces.
- **Images:** use `components/SafeImage.tsx` (wraps `next/image`; allow-listed hosts in `lib/image-hosts.ts` + `next.config.ts` `images.remotePatterns`, a plain `<img>` for others, a local placeholder on any load error) — never a bare `<img>` or `next/image` for catalog images; explicit width/height or `fill` to prevent shift.
- **Money:** values arrive as integer minor units + currency code (AGENTS.md invariant). Format exclusively via `formatMoney(minorUnits, currency)` / `minorToMajor` in `lib/money.ts` — the minor-unit exponent varies per currency (JPY 0, KWD 3; ADR-0046), so never divide by 100 inline and never hardcode a currency symbol (currency is per-storefront config, ADR-0015/0038).
- **Tailwind:** utilities in JSX are the styling layer — no CSS modules/styled-components. Shared design tokens via CSS variables per shadcn theming; if a class string needs reuse, extract a component, not an `@apply` class.

## 6. Blazor admin (differences only)

- Same gateway, same auth model, same money/ProblemDetails rules.
- Plain CRUD-grade UI is acceptable (ADR-0019) — no design-system ambitions; prefer QuickGrid/simple forms; saga-triggering buttons (approve RMA, refund) must disable on click and render the returned state (idempotent server, but don't invite double-clicks).

## Definition of done for a component

- [ ] Server Component unless interactivity demands otherwise; `'use client'` at the leaf
- [ ] Data via props from server fetch through the gateway; no client-side secrets
- [ ] Typed props; no `any`; no new UI library without justification
- [ ] Keyboard + focus behavior verified; labels/alt text present
- [ ] Loading/error states where the segment can be slow or fail; empty states exist; no layout shift
- [ ] Images via `SafeImage`; user-facing strings via next-intl messages
- [ ] Money through `formatMoney`; URL holds shareable state
- [ ] `pnpm lint && pnpm exec tsc --noEmit` clean

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
