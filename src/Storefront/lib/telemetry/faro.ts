// Browser RUM + error telemetry with the Grafana Faro Web SDK (audit P1-2; owner decisions 2026-11-15).
//
// STRICTLY NECESSARY AND ANONYMOUS (Q3): collected without a consent gate, so it must carry nothing that
// identifies a person —
//   - no session tracking and no user meta: Faro's SessionInstrumentation is not installed, nothing is persisted
//     (no localStorage/sessionStorage/cookie), and `beforeSend` strips any session/user meta that something else
//     might set; the `/rum` relay strips them again and never forwards the client IP;
//   - every item goes through lib/telemetry/scrub.ts (emails, tokens, query strings, identifier path segments);
//   - no replay, ever (Q4), and no user-action / frustration instrumentation (they read element text).
// Never inside a Stripe iframe: Faro only starts in the storefront's own top-level window (a framed copy of the
// storefront and any page on a Stripe host do nothing), Stripe URLs are ignored by the fetch/XHR tracing (no span,
// no `traceparent`), and errors raised by Stripe scripts are not reported (lib/telemetry/guards.ts).
//
// Transport: the browser posts batches to the same-origin `/rum` route (app/rum/route.ts), which scrubs, caps the
// size and relays to the collector's `faro` receiver. The collector is never exposed to the browser.

import {
  ConsoleInstrumentation,
  ErrorsInstrumentation,
  FetchTransport,
  LogLevel,
  NavigationInstrumentation,
  PerformanceInstrumentation,
  ViewInstrumentation,
  WebVitalsInstrumentation,
  browserMeta,
  initializeFaro,
  osMeta,
  sdkMeta,
  type Faro,
  type Meta,
  type MetaItem,
} from "@grafana/faro-web-sdk";
import { TracingInstrumentation } from "@grafana/faro-web-tracing";
import { RUM_PATH, STRIPE_URL_PATTERNS, beforeSend, mayStartFaro } from "./guards";
import { scrubUrl } from "./scrub";
import { SERVICE_NAME, SERVICE_NAMESPACE, serviceVersion } from "./release";

export interface FaroOptions {
  /** deployment.environment.name, resolved on the server at render time (one image runs in dev/test/prod). */
  environment: string;
  /** Extra origins (e.g. a separate public gateway origin) that receive `traceparent`; same-origin always does. */
  traceOrigins?: string[];
}

/** Browser context without fingerprinting detail: no raw user-agent string, no language, no brands. */
const browserLiteMeta: MetaItem = () => {
  let full: Meta["browser"] | undefined;
  try {
    const meta = typeof browserMeta === "function" ? browserMeta() : browserMeta;
    full = meta.browser;
  } catch {
    full = undefined;
  }
  return {
    browser: {
      name: full?.name,
      version: full?.version,
      os: full?.os,
      mobile: full?.mobile,
      viewportWidth: `${window.innerWidth}`,
      viewportHeight: `${window.innerHeight}`,
    },
  };
};

/** The page URL with no query string, no fragment and identifier segments redacted. */
const pageMeta: MetaItem = () => ({ page: { url: scrubUrl(window.location.href) } });

/**
 * faro-web-tracing samples spans by `meta.session.attributes.isSampled`. With session tracking off there is no
 * session, which would make every span NOT_RECORD — and the injected `traceparent` would then carry the
 * not-sampled flag, so the gateway (ParentBased sampler) would drop the backend trace too. This meta marks the
 * page as sampled WITHOUT any id; `beforeSend` strips it from what is sent.
 */
const sampledMeta: MetaItem = { session: { attributes: { isSampled: "true" } } };

function originMatcher(origin: string): RegExp {
  const escaped = origin.replace(/\/$/, "").replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return new RegExp(`^${escaped}(/|$)`);
}

let started: Faro | null = null;

/** Starts Faro once per page load (idempotent). Returns null where it must not run. */
export function startFaro(options: FaroOptions): Faro | null {
  if (typeof window === "undefined") return null;
  if (started) return started;
  if (!mayStartFaro(window)) return null;

  started = initializeFaro({
    app: {
      name: SERVICE_NAME,
      namespace: SERVICE_NAMESPACE,
      version: serviceVersion(),
      environment: options.environment,
    },
    transports: [new FetchTransport({ url: RUM_PATH })],
    // Replaces Faro's default metas (which include the raw user agent, language and the full page URL).
    metas: [browserLiteMeta, osMeta, pageMeta, sampledMeta, sdkMeta],
    sessionTracking: { enabled: false, persistent: false },
    trackGeolocation: false,
    trackResources: false,
    webVitalsInstrumentation: { trackAttributionSources: false },
    consoleInstrumentation: {
      // console.error only: debug/trace/log/info/warn stay in the browser.
      disabledLevels: [LogLevel.TRACE, LogLevel.DEBUG, LogLevel.LOG, LogLevel.INFO, LogLevel.WARN],
    },
    ignoreUrls: STRIPE_URL_PATTERNS,
    ignoreErrors: [/stripe\.(com|network)/i],
    beforeSend,
    instrumentations: [
      new ErrorsInstrumentation(), // uncaught errors + unhandled rejections
      new ConsoleInstrumentation(),
      new WebVitalsInstrumentation(),
      new ViewInstrumentation(),
      new NavigationInstrumentation(), // page views + route changes (the breadcrumb trail)
      new PerformanceInstrumentation(), // navigation + fetch/XHR resource timings
      new TracingInstrumentation({
        instrumentationOptions: {
          // Same-origin requests always get `traceparent`; these extra origins (a separate public gateway) too.
          propagateTraceHeaderCorsUrls: (options.traceOrigins ?? []).filter(Boolean).map(originMatcher),
        },
      }),
    ],
  });
  return started;
}
