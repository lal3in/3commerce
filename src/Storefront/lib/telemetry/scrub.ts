// Anonymising scrubber for browser telemetry (audit P1-2, owner decision Q3 2026-11-15).
//
// Browser error + performance telemetry is collected WITHOUT a consent gate because it is strictly necessary
// AND anonymous: no user or session identifiers, no IP, no replay, no PII in URLs. This module is the one place
// that makes "anonymous" true. It runs twice, with the same code:
//   1. in the browser, as Faro's `beforeSend` hook (lib/telemetry/faro.ts) — so nothing identifying leaves the page;
//   2. in the `/rum` relay (app/rum/route.ts) over the whole batch — so a tampered or outdated client cannot
//      push identifiers past us into the collector.
// Pure functions only (no DOM, no Node APIs): the same file is bundled into the client and the server, and the
// unit spec (e2e/telemetry-scrub.spec.ts) imports it directly.
//
// What it does:
//   - drops identifier keys outright: session / user / enduser / email / IP / cookie / authorization / raw
//     user-agent, in plain objects (Faro meta) and in OTLP attribute lists ({ key, value } entries);
//   - strips the query string and fragment from every URL (they carry search terms, tokens, emails);
//   - redacts tokens in URL paths (UUIDs, long hex, mixed-case opaque ids like Stripe intents) and in free text
//     (JWTs, bearer tokens, `token=…`-style pairs), and every email address anywhere;
//   - never touches trace/span ids — they are what links a browser error to the backend trace.
// It always returns NEW objects: Faro hands `beforeSend` items whose `meta` can be the live metas object, and
// mutating that would e.g. remove the sampling flag the tracer reads.

export const REDACTED_EMAIL = "[email]";
export const REDACTED_TOKEN = "[token]";
export const REDACTED_ID = "[id]";

/** Keys (lower-cased) whose value is an identifier: the key and its value are dropped wherever they appear. */
const DROP_KEYS = new Set([
  "session",
  "sessionid",
  "session_id",
  "session.id",
  "x-faro-session-id",
  "user",
  "userid",
  "user_id",
  "user.id",
  "user.email",
  "user.name",
  "user.full_name",
  "user.fullname",
  "user.roles",
  "user.hash",
  "username",
  "email",
  "enduser.id",
  "enduser.role",
  "enduser.scope",
  "ip",
  "ipaddress",
  "client.address",
  "client.ip",
  "http.client_ip",
  "net.peer.ip",
  "net.sock.peer.addr",
  "network.peer.address",
  "x-forwarded-for",
  "x-real-ip",
  "forwarded",
  "cookie",
  "set-cookie",
  "authorization",
  "useragent",
  "user_agent.original",
  "http.user_agent",
  "process.runtime.version", // Faro sets it to the raw user-agent string
  "browser.language",
  "language",
  "brands",
  "browser.brands",
  "geo",
  "location",
]);

/** Keys whose values are trace correlation ids — left exactly as they are. */
const KEEP_VERBATIM_KEYS = new Set([
  "traceid",
  "spanid",
  "parentspanid",
  "trace_id",
  "span_id",
  "parent_span_id",
  "traceparent",
  "tracestate",
]);

const EMAIL = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g;
const HAS_EMAIL = /[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/;
const JWT = /\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*/g;
const BEARER = /\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+/gi;
// `token=abc`, `"password": "x"`, `session_id: x` in free text (console messages, error strings).
const SECRET_PAIR =
  /\b((?:access_|refresh_|id_|reset_|verify_|verification_)?token|password|passwd|secret|client_secret|api_?key|session(?:_?id)?|signature|otp)(\s*[=:]\s*"?)([^&\s"',;]+)/gi;
// An absolute URL, or a root-relative path, followed by a query and/or fragment.
const ABSOLUTE_URL = /\b(https?:\/\/[^\s"'<>?#]+)([?#][^\s"'<>]*)?/gi;
const RELATIVE_URL_WITH_QUERY = /(^|[\s"'(=])(\/[^\s"'<>?#]*)[?#][^\s"'<>)]*/g;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const LONG_HEX = /^[0-9a-f]{32,}$/i;
const OPAQUE = /^[A-Za-z0-9_-]{20,}$/;

/** A path segment that is an identifier or a secret rather than a route word or a product slug. */
function redactSegment(segment: string): string {
  if (!segment) return segment;
  let decoded = segment;
  try {
    decoded = decodeURIComponent(segment);
  } catch {
    /* keep the raw segment */
  }
  if (HAS_EMAIL.test(decoded)) return REDACTED_EMAIL;
  if (UUID.test(decoded)) return REDACTED_ID;
  if (LONG_HEX.test(decoded)) return REDACTED_TOKEN;
  // Mixed-case + digit opaque ids (Stripe pi_…/seti_…, base64url tokens). Product slugs are lower-case words.
  if (OPAQUE.test(decoded) && /[a-z]/.test(decoded) && /[A-Z]/.test(decoded) && /[0-9]/.test(decoded)) {
    return REDACTED_TOKEN;
  }
  return segment;
}

function redactPath(path: string): string {
  return path
    .split("/")
    .map((segment) => redactSegment(segment))
    .join("/");
}

/**
 * Scrubs one URL: drops the query string and fragment, redacts identifier/token path segments.
 * Works on absolute and root-relative URLs; anything else goes through {@link scrubText}.
 */
export function scrubUrl(value: string): string {
  const trimmed = value.trim();
  const absolute = /^(https?:\/\/[^/?#]*)([^?#]*)/i.exec(trimmed);
  if (absolute) {
    // Credentials in the authority (`https://user:pass@host`) are dropped too.
    const authority = absolute[1].replace(/\/\/[^/@]*@/, "//");
    return authority + redactPath(absolute[2]);
  }
  if (trimmed.startsWith("/")) {
    return redactPath(trimmed.split(/[?#]/, 1)[0]);
  }
  return scrubText(value);
}

/** Scrubs free text (error messages, console output, stack frames): emails, tokens, URL queries. */
export function scrubText(value: string): string {
  return value
    .replace(JWT, REDACTED_TOKEN)
    .replace(BEARER, (_m, scheme: string) => `${scheme} ${REDACTED_TOKEN}`)
    .replace(ABSOLUTE_URL, (url: string) => scrubUrl(url))
    .replace(RELATIVE_URL_WITH_QUERY, (_m, lead: string, path: string) => lead + redactPath(path))
    .replace(SECRET_PAIR, (_m, key: string, sep: string) => `${key}${sep}[redacted]`)
    .replace(EMAIL, REDACTED_EMAIL);
}

const URL_KEYS = new Set([
  "url",
  "href",
  "page.url",
  "http.url",
  "url.full",
  "url.path",
  "url.query",
  "http.target",
  "referrer",
  "referer",
  "document.referrer",
  "name", // performance resource entries are named by their URL
  "filename",
  "resource_url",
]);

function scrubString(key: string | undefined, value: string): string {
  const k = key?.toLowerCase();
  if (k && KEEP_VERBATIM_KEYS.has(k)) return value;
  if (k === "url.query") return "";
  if (k && URL_KEYS.has(k)) return scrubUrl(value);
  return scrubText(value);
}

function isDropKey(key: string): boolean {
  const k = key.toLowerCase();
  return DROP_KEYS.has(k) || k.startsWith("user.") || k.startsWith("enduser.") || k.startsWith("session.");
}

/** An OTLP `{ key, value }` attribute entry (span/resource attributes in Faro's `traces` payload). */
function isOtlpKeyValue(value: unknown): value is { key: string; value: unknown } {
  return (
    typeof value === "object" &&
    value !== null &&
    !Array.isArray(value) &&
    typeof (value as { key?: unknown }).key === "string" &&
    "value" in (value as object) &&
    Object.keys(value as object).length <= 2
  );
}

/**
 * Deep-scrubs any telemetry value — a Faro TransportItem, a whole `/rum` batch body, an OTLP trace export.
 * Returns a new value; the input is never mutated.
 */
export function scrubTelemetry<T>(value: T, key?: string, depth = 0): T {
  if (depth > 32) return undefined as T; // pathological nesting: drop rather than recurse forever
  if (typeof value === "string") return scrubString(key, value) as T;
  if (Array.isArray(value)) {
    const out: unknown[] = [];
    for (const entry of value) {
      if (isOtlpKeyValue(entry)) {
        if (isDropKey(entry.key)) continue;
        out.push({ key: entry.key, value: scrubTelemetry(entry.value, entry.key, depth + 1) });
        continue;
      }
      out.push(scrubTelemetry(entry, key, depth + 1));
    }
    return out as T;
  }
  if (typeof value === "object" && value !== null) {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      if (isDropKey(k)) continue;
      // OTLP AnyValue wrappers ({ stringValue }) inherit the attribute's key so URL attributes are URL-scrubbed.
      const childKey = k === "stringValue" || k === "value" ? key : k;
      out[k] = scrubTelemetry(v, childKey, depth + 1);
    }
    return out as T;
  }
  return value;
}

/** Faro `beforeSend` hook shape, kept structural so this module stays free of SDK imports. */
export function scrubTransportItem<T extends { meta?: unknown; payload?: unknown }>(item: T): T {
  return scrubTelemetry(item);
}
