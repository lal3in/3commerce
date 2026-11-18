# 0066 — Storefront telemetry: anonymous browser RUM (Faro) through a same-origin relay, and SSR tracing

Status: Accepted — implemented
Area: Observability / Storefront / privacy
Extends: [0064](./0064-release-identity-on-telemetry.md) (release identity), [0065](./0065-self-hosted-lgtm-telemetry-alerting-and-browser-rum.md) (the collector faro receiver), [0010](./0010-nextjs-ssr-storefront.md) (Next.js storefront)
Source: observability capability audit (`docs/reviews/observability-capability-audit.md`) roadmap **P1-2** and **P1-3**;
owner decisions 2026-11-15 (Q3, Q4, Q7)

## Context

The storefront was dark: no browser errors, no Web Vitals, no breadcrumbs, no `instrumentation.ts`, no error
boundaries. Traces started at the gateway, so neither the shopper's browser nor the Next.js render was part of a
trace, and an error page could not give support anything to search for. The owner decided (2026-11-15):

- **Q3** — browser error and performance telemetry is **strictly necessary and anonymised**: collected WITHOUT a
  consent gate, with no user or session identifiers, no IP stored, no replay, and no PII in URLs (query strings
  stripped, tokens redacted).
- **Q4** — no session replay, ever.
- **Q7** — `@grafana/faro-web-sdk` + `@grafana/faro-web-tracing` and `@vercel/otel` are approved.

## Decision

1. **Browser RUM = Grafana Faro** (`lib/telemetry/faro.ts`, started by `components/telemetry/FaroInit.tsx` from the
   root layout, `FARO_ENABLED=false` turns it off): uncaught errors and rejections, `console.error`, fetch/XHR
   spans, Web Vitals, page views and route changes (the breadcrumb trail). Faro's default instrumentation set is NOT
   used: no SessionInstrumentation, no user-action/frustration tracking (they read element text), no CSP reports.
2. **Anonymity is enforced twice, by one module** (`lib/telemetry/scrub.ts`): Faro's `beforeSend` in the browser and
   again in the relay. It drops session/user/enduser/email/IP/cookie/authorization/raw-user-agent keys (Faro meta
   and OTLP attribute lists), strips every URL's query and fragment, redacts identifier path segments (UUIDs, long
   hex, mixed-case opaque ids) and, in free text, emails, JWTs, bearer tokens and `token=`-style pairs; trace/span
   ids are kept. It always returns a new object (Faro can hand it the live metas). Faro's own metas are replaced
   with coarse ones (browser name/version/OS/mobile/viewport, the scrubbed page URL). Nothing is persisted in the
   browser.
3. **Sampling without a session**: faro-web-tracing samples by `meta.session.attributes.isSampled`; with no session
   every span would be NOT_RECORD and the injected `traceparent` would carry the not-sampled flag, making the
   gateway's ParentBased sampler drop the backend trace too. A static meta `{ session: { attributes: { isSampled:
   "true" } } }` (no id) keeps tracing on; `beforeSend` strips it from what is sent.
4. **Transport = a same-origin relay, `POST /rum`** (`app/rum/route.ts`). It refuses cross-site posts (403), caps the
   body at 256 KiB (413), requires JSON (400), scrubs the batch again and forwards it to `FARO_RECEIVER_URL` (the
   collector's `faro` receiver, internal port 8027, which accepts POSTs at `/`; dev default
   `http://localhost:8027`) with **no client header at all** — no IP, `X-Forwarded-*`, cookie, user agent or
   `x-faro-session-id`. It always answers 202 otherwise: a missing or failing receiver is dropped silently, so Faro
   never retries in a loop and the page never notices. The collector is never exposed to the browser.
5. **Stripe**: Faro starts only in the storefront's own top-level window (not in any frame, not on a Stripe host);
   Stripe URLs are in `ignoreUrls` (no span, no `traceparent`), and errors whose frames are all Stripe scripts are
   dropped. A cross-origin Stripe Elements iframe is out of reach of our code anyway; the guards make that explicit
   for SAQ-A.
6. **SSR tracing = `@vercel/otel`** in `instrumentation.ts` (Node runtime): Next's built-in render/route/fetch spans
   exported over OTLP **http/protobuf** when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (`dev-up.sh` and `e2e-verify.sh`
   point it at the collector's `:4318`; unset = nothing exported). `fetch` propagates `traceparent` **to the gateway
   only** (`propagateContextUrls: [GATEWAY_URL]`), so render → gateway → services → bus is one trace. Resource =
   ADR-0064: `service.name=storefront`, `service.namespace=3commerce`, `service.version`, `vcs.ref.head.revision`,
   `deployment.environment.name`, `service.instance.id`, plus a `Release identity:` startup line.
7. **Release identity in the bundle**: `next.config.ts` stamps `NEXT_PUBLIC_SERVICE_VERSION` (`<Version>+<sha7>[-dirty]`)
   and `NEXT_PUBLIC_VCS_REVISION` from `GIT_SHA`/`VERSION` (Docker build args, now declared in the build stage) or
   git. Faro events carry it as `app.version`. `deployment.environment.name` is runtime config (one image is
   promoted dev → test → prod), resolved on the server per render (explicit `OTEL_RESOURCE_ATTRIBUTES` wins, then
   `DEPLOYMENT_ENVIRONMENT`, then `NODE_ENV`; a production server with nothing explicit counts as `prod`) and passed
   to the browser as a prop.
8. **Error pages**: `app/error.tsx` (inside the root layout, `errorPage` messages in all six locales) and
   `app/global-error.tsx` (replaces the root layout; embedded copy in `lib/telemetry/error-copy.ts`, kept equal to the
   catalogs by a unit test) show a **support reference** — the server error's digest, else the SSR trace id from
   `<meta name="traceparent">` (`experimental.clientTraceMetadata`), else a random id — never the message. The error
   is pushed to Faro with that reference; `instrumentation.ts` `onRequestError` logs every server error as one JSON
   line with digest + trace id (query string and message scrubbed) and records it on the active span.
9. **Source maps**: `productionBrowserSourceMaps: true`, but the `postbuild` step (`scripts/move-sourcemaps.mjs`) moves
   every `.map` out of `<distDir>/static` (served, and copied into the image) to `<distDir>/sourcemaps`, and
   `middleware.ts` answers `/_next/static/**.map` with 404 in production. Uploading them for symbolication is a
   documented manual/CI step (`docs/help/getting-started.md`, "Storefront telemetry").
10. **Error probes** `/probe/error` (server) and `/probe/client-error` (browser) exist for the E2E suite and answer 404
    when the environment is `prod`.

## Rejected

- Sending from the browser straight to the collector or Grafana Alloy: exposes an ingest endpoint publicly, leaks the
  client IP to it, and puts the anonymity guarantee in the hands of whatever client code runs.
- A per-page random id as `session.id`: allowed by the decision but not needed — nothing correlates on it.
- Gating on the Analytics consent category (the `track()` model): the owner ruled this telemetry strictly necessary
  provided it stays anonymous (Q3).
- Sentry SDKs: a second instrumentation path beside OTel (audit comparison table).

## Consequences

- Browser → gateway traces join; a shopper's support reference leads to a log line and a trace.
- Until the collector's `faro` receiver ships (feature/observability-prod-backend-and-alerting), `/rum` drops
  batches silently; SSR spans already flow to Tempo.
- Web Vitals and errors carry no identifier, so "how many shoppers" questions cannot be answered from RUM — by design.
- Pinned by `e2e/telemetry-scrub.spec.ts` (e2e-verify **A7d**, no stack), the build check **A7c** and the live
  `e2e/observability-storefront.spec.ts` (**L20**).
