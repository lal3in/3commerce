// The storefront's half of the GlitchTip error inbox (ADR-0067, owner decision Q5) — SERVER-SIDE ONLY.
//
// No Sentry browser SDK: the browser already reports errors through Faro (ADR-0066), anonymously, to the
// same-origin /rum relay. The relay hands each Faro EXCEPTION to GlitchTip as a Sentry "store" event, and
// instrumentation.ts does the same for SSR errors. So the shopper's browser never talks to GlitchTip, no second
// instrumentation path runs in the page, and every event has already been through lib/telemetry/scrub.ts
// (no session, user, IP, query string, email or token) before it is converted here. Release identity (ADR-0064):
// `release` = the build's service.version, `environment` = deployment.environment.name. The OTel trace id goes
// into the event's trace context and a searchable `trace_id` tag; the error page's support reference into
// `support_ref`.
//
// SENTRY_DSN (server env, never NEXT_PUBLIC_): the storefront project's DSN. Unset = nothing is sent.
// Fail-soft: an unreachable inbox costs one bounded timeout and is otherwise ignored.

import { scrubTelemetry, scrubText, scrubUrl } from "./scrub";

/** Events relayed per /rum batch at most (a page in an error loop must not flood the inbox). */
export const MAX_EVENTS_PER_BATCH = 10;
const SEND_TIMEOUT_MS = 3_000;
const CLIENT = "3commerce-storefront-relay/1.0";

export interface Dsn {
  storeUrl: string;
  publicKey: string;
}

/** `http://<key>@host[:port][/path]/<project>` → the store endpoint + key; null when unset or malformed. */
export function parseDsn(raw: string | undefined): Dsn | null {
  const value = (raw ?? "").trim();
  if (!value) return null;
  try {
    const url = new URL(value);
    const segments = url.pathname.split("/").filter(Boolean);
    const project = segments.pop();
    if (!url.username || !project || !/^\d+$/.test(project)) return null;
    const prefix = segments.length ? `/${segments.join("/")}` : "";
    return { storeUrl: `${url.protocol}//${url.host}${prefix}/api/${project}/store/`, publicKey: url.username };
  } catch {
    return null;
  }
}

export interface SentryFrame {
  filename?: string;
  abs_path?: string;
  function?: string;
  lineno?: number;
  colno?: number;
  in_app?: boolean;
}

export interface SentryEvent {
  event_id: string;
  timestamp: string;
  platform: "javascript" | "node";
  level: "error" | "fatal";
  logger: string;
  release?: string;
  environment?: string;
  server_name: string;
  exception: { values: { type: string; value: string; stacktrace?: { frames: SentryFrame[] } }[] };
  tags: Record<string, string>;
  contexts: Record<string, Record<string, string>>;
  extra?: Record<string, string>;
  fingerprint?: string[];
}

interface FaroFrame {
  filename?: unknown;
  function?: unknown;
  lineno?: unknown;
  colno?: unknown;
}

interface FaroException {
  type?: unknown;
  value?: unknown;
  timestamp?: unknown;
  fatal?: unknown;
  fingerprint?: unknown;
  stacktrace?: { frames?: FaroFrame[] };
  trace?: { trace_id?: unknown; span_id?: unknown };
  context?: Record<string, unknown>;
}

interface FaroBatch {
  meta?: {
    app?: { version?: unknown; environment?: unknown };
    page?: { url?: unknown };
    browser?: { name?: unknown; version?: unknown; os?: unknown; mobile?: unknown };
  };
  exceptions?: FaroException[];
}

const str = (value: unknown): string | undefined => (typeof value === "string" && value.length > 0 ? value : undefined);
const num = (value: unknown): number | undefined => (typeof value === "number" && Number.isFinite(value) ? value : undefined);
const HEX32 = /^[0-9a-f]{32}$/i;
const HEX16 = /^[0-9a-f]{16}$/i;

function eventId(): string {
  return globalThis.crypto.randomUUID().replace(/-/g, "");
}

function isoTimestamp(value: unknown): string {
  const parsed = typeof value === "string" ? Date.parse(value) : NaN;
  return new Date(Number.isNaN(parsed) ? Date.now() : parsed).toISOString();
}

function frame(filename: string | undefined, fn: string | undefined, lineno?: number, colno?: number): SentryFrame {
  const file = filename ? scrubUrl(filename) : undefined;
  return {
    filename: file,
    abs_path: file,
    function: fn ? scrubText(fn) : undefined,
    lineno,
    colno,
    in_app: !!file && !/node_modules|\/_next\/static\/chunks\/(framework|main|webpack|polyfills)/.test(file),
  };
}

function traceContext(traceId: string | undefined, spanId: string | undefined) {
  const contexts: Record<string, Record<string, string>> = {};
  const tags: Record<string, string> = {};
  if (traceId && HEX32.test(traceId)) {
    contexts.trace = { trace_id: traceId.toLowerCase(), ...(spanId && HEX16.test(spanId) ? { span_id: spanId.toLowerCase() } : {}) };
    tags.trace_id = traceId.toLowerCase();
  }
  return { contexts, tags };
}

/**
 * Converts the EXCEPTIONS of one (already scrubbed) Faro batch into Sentry store events. Logs, measurements,
 * events and traces are not errors and stay out. Scrubs again, so it is safe on any input.
 */
export function faroBatchToEvents(input: unknown): SentryEvent[] {
  if (typeof input !== "object" || input === null) return [];
  const batch = scrubTelemetry(input) as FaroBatch;
  const exceptions = Array.isArray(batch.exceptions) ? batch.exceptions : [];
  const app = batch.meta?.app ?? {};
  const browser = batch.meta?.browser ?? {};
  const page = str(batch.meta?.page?.url);

  return exceptions.slice(0, MAX_EVENTS_PER_BATCH).map((ex) => {
    const frames = (Array.isArray(ex.stacktrace?.frames) ? ex.stacktrace.frames : [])
      .map((f) => frame(str(f.filename), str(f.function), num(f.lineno), num(f.colno)))
      .reverse(); // Faro lists the throwing frame first; Sentry wants it last.
    const { contexts, tags } = traceContext(str(ex.trace?.trace_id), str(ex.trace?.span_id));
    tags.runtime = "browser";
    if (page) tags["page.url"] = scrubUrl(page);
    if (str(browser.name)) {
      contexts.browser = { name: str(browser.name)!, ...(str(browser.version) ? { version: str(browser.version)! } : {}) };
    }
    if (str(browser.os)) contexts.os = { name: str(browser.os)! };
    const context: Record<string, string> = {};
    for (const [key, value] of Object.entries(ex.context ?? {})) {
      if (typeof value === "string") context[key] = scrubText(value);
    }
    if (context.reference) tags.support_ref = context.reference;
    if (context.boundary) tags.boundary = context.boundary;

    return {
      event_id: eventId(),
      timestamp: isoTimestamp(ex.timestamp),
      platform: "javascript",
      level: ex.fatal === true ? "fatal" : "error",
      logger: "browser",
      release: str(app.version),
      environment: str(app.environment),
      server_name: "storefront-browser",
      exception: {
        values: [
          {
            type: str(ex.type) ?? "Error",
            value: scrubText(str(ex.value) ?? ""),
            ...(frames.length ? { stacktrace: { frames } } : {}),
          },
        ],
      },
      tags,
      contexts,
      ...(Object.keys(context).length ? { extra: context } : {}),
      ...(str(ex.fingerprint) ? { fingerprint: [str(ex.fingerprint)!] } : {}),
    };
  });
}

// V8 stack lines: "    at fn (file:line:col)" or "    at file:line:col".
const V8_FRAME = /^\s*at (?:(.+?) \()?(.+?):(\d+):(\d+)\)?$/;

/** Frames of a Node error stack, oldest first (Sentry order). */
export function nodeFrames(stack: string | undefined): SentryFrame[] {
  return (stack ?? "")
    .split("\n")
    .map((line) => V8_FRAME.exec(line))
    .filter((m): m is RegExpExecArray => m !== null)
    .map((m) => frame(m[2], m[1], Number(m[3]), Number(m[4])))
    .reverse();
}

export interface ServerErrorDetails {
  digest?: string | null;
  traceId?: string | null;
  spanId?: string | null;
  routePath?: string | null;
  routeType?: string | null;
  method?: string | null;
  release: string;
  environment: string;
}

/** One SSR / route-handler error (instrumentation.ts onRequestError) as a Sentry store event. */
export function serverErrorToEvent(error: Error, details: ServerErrorDetails): SentryEvent {
  const frames = nodeFrames(error.stack);
  const { contexts, tags } = traceContext(details.traceId ?? undefined, details.spanId ?? undefined);
  tags.runtime = "node";
  if (details.routePath) tags["http.route"] = details.routePath;
  if (details.routeType) tags["route.type"] = details.routeType;
  if (details.method) tags["http.request.method"] = details.method;
  if (details.digest) tags.support_ref = details.digest;
  return {
    event_id: eventId(),
    timestamp: new Date().toISOString(),
    platform: "node",
    level: "error",
    logger: "ssr",
    release: details.release,
    environment: details.environment,
    server_name: "storefront",
    exception: {
      values: [
        {
          type: error.name || "Error",
          value: scrubText(error.message ?? ""),
          ...(frames.length ? { stacktrace: { frames } } : {}),
        },
      ],
    },
    tags,
    contexts,
  };
}

/** Posts events to the inbox. Never throws; resolves when all were tried. */
export async function sendToInbox(events: SentryEvent[], dsn: Dsn | null = parseDsn(process.env.SENTRY_DSN)): Promise<void> {
  if (!dsn || events.length === 0) return;
  const auth = `Sentry sentry_version=7, sentry_client=${CLIENT}, sentry_key=${dsn.publicKey}`;
  await Promise.allSettled(
    events.map((event) =>
      fetch(dsn.storeUrl, {
        method: "POST",
        headers: { "content-type": "application/json", "x-sentry-auth": auth },
        body: JSON.stringify(event),
        signal: AbortSignal.timeout(SEND_TIMEOUT_MS),
        cache: "no-store",
        // The hop to the inbox is not part of the shopper's trace.
        opentelemetry: { ignore: true },
      } as RequestInit),
    ),
  );
}
