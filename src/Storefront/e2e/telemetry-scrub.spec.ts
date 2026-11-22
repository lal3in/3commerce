import { readFileSync } from "node:fs";
import path from "node:path";
import { test, expect } from "../e2e-support/fixtures";
import { REDACTED_EMAIL, REDACTED_ID, REDACTED_TOKEN, scrubTelemetry, scrubText, scrubUrl } from "../lib/telemetry/scrub";
import { beforeSend, isStripeUrl, mayStartFaro } from "../lib/telemetry/guards";
import { mapEnvironment, parseResourceAttributes, resolveEnvironment } from "../lib/telemetry/release";
import { errorReference, traceIdFromTraceparent } from "../lib/telemetry/error-reference";
import { ERROR_COPY, errorCopyFor } from "../lib/telemetry/error-copy";
import { MAX_EVENTS_PER_BATCH, faroBatchToEvents, nodeFrames, parseDsn, serverErrorToEvent } from "../lib/telemetry/error-inbox";

/**
 * Unit tests for the browser-telemetry anonymiser and its helpers (audit P1-2/P1-3, owner decision Q3: strictly
 * necessary + anonymous). Pure functions — needs no running stack, like hermetic-images.spec.ts.
 */
const EMAIL = "jane.doe+shop@example.com";
const TRACE_ID = "4bf92f3577b34da6a3ce929d0e0e4736";
const SPAN_ID = "00f067aa0ba902b7";

test.describe("telemetry scrubber (unit)", () => {
  test("URLs lose their query string and fragment; identifier path segments are redacted", () => {
    expect(scrubUrl("https://shop.example/search?q=red+shoes&email=a%40b.com#top")).toBe("https://shop.example/search");
    expect(scrubUrl("/account/verify?token=abc123")).toBe("/account/verify");
    expect(scrubUrl("http://localhost:3000/orders/0198f3a2-5b6c-7d8e-9f01-23456789abcd/support")).toBe(
      `http://localhost:3000/orders/${REDACTED_ID}/support`,
    );
    expect(scrubUrl("/api/dev-pay-intent/pi_3QxYzAbCdEf12345GhIj")).toBe(`/api/dev-pay-intent/${REDACTED_TOKEN}`);
    expect(scrubUrl("/reset/0123456789abcdef0123456789abcdef")).toBe(`/reset/${REDACTED_TOKEN}`);
    expect(scrubUrl(`/u/${encodeURIComponent(EMAIL)}`)).toBe(`/u/${REDACTED_EMAIL}`);
    expect(scrubUrl("https://user:secret@host.example/p")).toBe("https://host.example/p");
    // Product slugs and route words survive — they are what makes a breadcrumb useful.
    expect(scrubUrl("/products/blue-widget-2000-pro-max-edition")).toBe("/products/blue-widget-2000-pro-max-edition");
  });

  test("free text loses emails, JWTs, bearer tokens, secret pairs and URL queries", () => {
    const jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
    const out = scrubText(
      `Failed for ${EMAIL}: Authorization Bearer abc.def-ghi token=s3cr3t password: hunter2 ${jwt} ` +
        `GET https://gw.example/api/x?session=zzz and /cart?coupon=SAVE10`,
    );
    for (const leaked of [EMAIL, "abc.def-ghi", "s3cr3t", "hunter2", jwt, "session=zzz", "coupon=SAVE10"]) {
      expect(out).not.toContain(leaked);
    }
    expect(out).toContain(REDACTED_EMAIL);
    expect(out).toContain("https://gw.example/api/x");
    expect(out).toContain("/cart");
    // Ordinary diagnostics are untouched.
    expect(scrubText("TypeError: x is undefined (status code: 500)")).toBe("TypeError: x is undefined (status code: 500)");
  });

  test("a Faro batch keeps no session, user, IP or user agent — and keeps trace ids", () => {
    const batch = {
      meta: {
        app: { name: "storefront", version: "1.0.0+abc1234", environment: "dev" },
        session: { id: "sess-123", attributes: { isSampled: "true" } },
        user: { id: "user-42", email: EMAIL },
        page: { url: "https://shop.example/checkout?email=" + encodeURIComponent(EMAIL) },
        browser: { name: "Chrome", userAgent: "Mozilla/5.0 (Macintosh)", language: "en-AU", brands: [{ brand: "x" }] },
      },
      exceptions: [
        {
          type: "Error",
          value: `boom for ${EMAIL}`,
          stacktrace: { frames: [{ filename: "https://shop.example/_next/static/chunks/a.js?v=1", lineno: 1 }] },
          trace: { trace_id: TRACE_ID, span_id: SPAN_ID },
          context: { reference: "1234567890" },
        },
      ],
      traces: {
        resourceSpans: [
          {
            resource: {
              attributes: [
                { key: "service.name", value: { stringValue: "storefront" } },
                { key: "user_agent.original", value: { stringValue: "Mozilla/5.0" } },
                { key: "session.id", value: { stringValue: "sess-123" } },
              ],
            },
            scopeSpans: [
              {
                spans: [
                  {
                    traceId: TRACE_ID,
                    spanId: SPAN_ID,
                    name: "HTTP GET",
                    attributes: [
                      { key: "http.url", value: { stringValue: "http://localhost:3000/api/order-status/x?token=t0k3n" } },
                      { key: "client.address", value: { stringValue: "203.0.113.7" } },
                      { key: "user.email", value: { stringValue: EMAIL } },
                    ],
                  },
                ],
              },
            ],
          },
        ],
      },
    };
    const before = JSON.stringify(batch);
    const scrubbed = scrubTelemetry(batch);
    const json = JSON.stringify(scrubbed);

    expect(JSON.stringify(batch)).toBe(before); // never mutates its input (Faro's live metas)
    for (const leaked of [EMAIL, "sess-123", "user-42", "203.0.113.7", "Mozilla", "en-AU", "t0k3n", "?v=1", "?email"]) {
      expect(json).not.toContain(leaked);
    }
    expect(scrubbed.meta).not.toHaveProperty("session");
    expect(scrubbed.meta).not.toHaveProperty("user");
    expect(scrubbed.meta.page.url).toBe("https://shop.example/checkout");
    expect(scrubbed.meta.app.version).toBe("1.0.0+abc1234");
    expect(json).toContain(TRACE_ID);
    expect(json).toContain(SPAN_ID);
    expect(json).toContain('"reference":"1234567890"');
    const spanAttrs = scrubbed.traces.resourceSpans[0].scopeSpans[0].spans[0].attributes.map((a) => a.key);
    expect(spanAttrs).toEqual(["http.url"]);
  });

  test("beforeSend drops Stripe-originated errors and scrubs everything else", () => {
    const stripe = {
      type: "exception",
      meta: {},
      payload: { value: "x", stacktrace: { frames: [{ filename: "https://js.stripe.com/v3/fingerprinted/js/a.js" }] } },
    };
    expect(beforeSend(stripe)).toBeNull();
    const ours = { type: "log", meta: { user: { email: EMAIL } }, payload: { message: `hi ${EMAIL}` } };
    const sent = beforeSend(ours);
    expect(JSON.stringify(sent)).not.toContain(EMAIL);
    expect(ours.meta.user.email).toBe(EMAIL);
  });

  test("Faro starts only in the storefront's own top-level window", () => {
    const top = {};
    expect(mayStartFaro({ self: top, top, location: { href: "http://localhost:3000/" } })).toBe(true);
    expect(mayStartFaro({ self: {}, top, location: { href: "http://localhost:3000/" } })).toBe(false); // framed
    expect(mayStartFaro({ self: top, top, location: { href: "https://js.stripe.com/v3/elements-inner-card.html" } })).toBe(
      false,
    );
    const throwing = {
      self: {},
      get top(): unknown {
        throw new Error("cross-origin");
      },
      location: { href: "http://localhost:3000/" },
    };
    expect(mayStartFaro(throwing)).toBe(false);
    expect(isStripeUrl("https://m.stripe.network/inner.html")).toBe(true);
    expect(isStripeUrl("https://notstripe.com.evil.example/")).toBe(false);
  });

  test("environment resolves like ReleaseIdentity: explicit OTEL_RESOURCE_ATTRIBUTES wins", () => {
    expect(parseResourceAttributes("a=1, deployment.environment.name=te%73t")).toEqual({
      a: "1",
      "deployment.environment.name": "test",
    });
    expect(mapEnvironment("Production")).toBe("prod");
    expect(mapEnvironment("Staging")).toBe("test");
    expect(resolveEnvironment({ OTEL_RESOURCE_ATTRIBUTES: "deployment.environment.name=ci", NODE_ENV: "production" })).toBe("ci");
    expect(resolveEnvironment({ DEPLOYMENT_ENVIRONMENT: "test", NODE_ENV: "production" })).toBe("test");
    expect(resolveEnvironment({ NODE_ENV: "development" })).toBe("dev");
    expect(resolveEnvironment({ NODE_ENV: "production" })).toBe("prod");
    expect(resolveEnvironment({})).toBe("prod");
  });

  test("the error reference prefers the digest, then the SSR trace id", () => {
    expect(traceIdFromTraceparent(`00-${TRACE_ID}-${SPAN_ID}-01`)).toBe(TRACE_ID);
    expect(traceIdFromTraceparent(`00-${"0".repeat(32)}-${SPAN_ID}-01`)).toBeNull();
    expect(traceIdFromTraceparent("garbage")).toBeNull();
    const doc = { querySelector: () => ({ getAttribute: () => `00-${TRACE_ID}-${SPAN_ID}-01` }) } as unknown as Document;
    expect(errorReference({ digest: "2847562" }, doc)).toBe("2847562");
    expect(errorReference({}, doc)).toBe(TRACE_ID);
    expect(errorReference({}, null)).toMatch(/^[0-9a-f]{16}$/);
  });

  test("global-error copy matches messages/<code>.json errorPage in every locale", () => {
    const codes = ["en", "zh", "yue", "de", "fr", "es"];
    expect(Object.keys(ERROR_COPY).sort()).toEqual([...codes].sort());
    for (const code of codes) {
      const catalog = JSON.parse(readFileSync(path.join(__dirname, "..", "messages", `${code}.json`), "utf8"));
      expect(catalog.errorPage, code).toEqual(ERROR_COPY[code]);
    }
    expect(errorCopyFor("de-AT")).toBe(ERROR_COPY.de);
    expect(errorCopyFor("pt-BR")).toBe(ERROR_COPY.en);
  });
});

/**
 * The storefront's half of the GlitchTip error inbox (ADR-0067): Faro exceptions and SSR errors become Sentry store
 * events server-side — anonymous like the RUM they come from, stamped with the release and the trace id.
 */
test.describe("error inbox conversion (unit)", () => {
  test("a DSN parses to the store endpoint and key; unset or malformed is off", () => {
    expect(parseDsn("http://685233df550a43af8410f48e60785466@localhost:8000/2")).toEqual({
      storeUrl: "http://localhost:8000/api/2/store/",
      publicKey: "685233df550a43af8410f48e60785466",
    });
    expect(parseDsn("https://k@errors.example/glitchtip/7")?.storeUrl).toBe("https://errors.example/glitchtip/api/7/store/");
    expect(parseDsn(undefined)).toBeNull();
    expect(parseDsn("  ")).toBeNull();
    expect(parseDsn("http://localhost:8000/2")).toBeNull(); // no key
    expect(parseDsn("not a url")).toBeNull();
  });

  test("Faro exceptions become anonymous events with release, environment, trace id and support reference", () => {
    const batch = {
      meta: {
        app: { name: "storefront", version: "1.0.0+abc1234", environment: "dev" },
        page: { url: `https://shop.example/orders/0198f3a2-5b6c-7d8e-9f01-23456789abcd?email=${EMAIL}` },
        browser: { name: "Chrome", version: "131", os: "macOS", userAgent: "Mozilla/5.0 (secret)" },
        session: { id: "sess-123", attributes: { isSampled: "true" } },
        user: { email: EMAIL, id: "u-1" },
      },
      logs: [{ message: "not an error" }],
      exceptions: [
        {
          type: "TypeError",
          value: `Cannot read x of undefined for ${EMAIL} token=abc`,
          timestamp: "2026-11-19T01:02:03.000Z",
          stacktrace: {
            frames: [
              { filename: "https://shop.example/_next/static/chunks/app/page-1a2b.js?v=1", function: "onClick", lineno: 10, colno: 5 },
              { filename: "https://shop.example/_next/static/chunks/framework-9f.js", function: "dispatch", lineno: 1, colno: 2 },
            ],
          },
          trace: { trace_id: TRACE_ID, span_id: SPAN_ID },
          context: { reference: "ref-7f3a", boundary: "error-page" },
        },
      ],
    };

    const events = faroBatchToEvents(batch);
    expect(events).toHaveLength(1);
    const [event] = events;
    expect(event.platform).toBe("javascript");
    expect(event.release).toBe("1.0.0+abc1234");
    expect(event.environment).toBe("dev");
    expect(event.timestamp).toBe("2026-11-19T01:02:03.000Z");
    expect(event.contexts.trace).toEqual({ trace_id: TRACE_ID, span_id: SPAN_ID });
    expect(event.tags.trace_id).toBe(TRACE_ID);
    expect(event.tags.support_ref).toBe("ref-7f3a");
    expect(event.tags["page.url"]).toBe(`https://shop.example/orders/${REDACTED_ID}`);
    expect(event.exception.values[0].type).toBe("TypeError");
    // Sentry order: the throwing frame LAST; framework chunks are not in-app; no query strings.
    const frames = event.exception.values[0].stacktrace!.frames;
    expect(frames.map((f) => f.function)).toEqual(["dispatch", "onClick"]);
    expect(frames[1]).toMatchObject({ filename: "https://shop.example/_next/static/chunks/app/page-1a2b.js", in_app: true });
    expect(frames[0].in_app).toBe(false);

    const serialized = JSON.stringify(events);
    for (const leaked of [EMAIL, "sess-123", "u-1", "userAgent", "secret", "token=abc", "?v=1", "email="]) {
      expect(serialized).not.toContain(leaked);
    }
  });

  test("a batch without exceptions sends nothing, and a flood is capped", () => {
    expect(faroBatchToEvents({ logs: [{ message: "x" }], measurements: [{}] })).toEqual([]);
    expect(faroBatchToEvents(null)).toEqual([]);
    const flood = { exceptions: Array.from({ length: 50 }, (_, i) => ({ type: "Error", value: `e${i}` })) };
    expect(faroBatchToEvents(flood)).toHaveLength(MAX_EVENTS_PER_BATCH);
    // A malformed trace id is dropped rather than sent.
    expect(faroBatchToEvents({ exceptions: [{ type: "E", value: "v", trace: { trace_id: "nope" } }] })[0].contexts.trace).toBeUndefined();
  });

  test("an SSR error becomes a node event with digest, route and trace id; its message is scrubbed", () => {
    const error = new Error(`render failed for ${EMAIL}`);
    error.stack = [
      `Error: render failed for ${EMAIL}`,
      "    at Page (/app/.next/server/app/page.js:12:34)",
      "    at async render (/app/node_modules/next/dist/server/render.js:5:6)",
    ].join("\n");
    expect(nodeFrames(error.stack).map((f) => f.function)).toEqual(["async render", "Page"]);

    const event = serverErrorToEvent(error, {
      digest: "1234567890",
      traceId: TRACE_ID,
      spanId: SPAN_ID,
      routePath: "/probe/error",
      routeType: "render",
      method: "GET",
      release: "1.0.0+abc1234",
      environment: "dev",
    });
    expect(event.platform).toBe("node");
    expect(event.tags).toMatchObject({ trace_id: TRACE_ID, support_ref: "1234567890", "http.route": "/probe/error", runtime: "node" });
    expect(event.release).toBe("1.0.0+abc1234");
    expect(event.exception.values[0].value).toBe(`render failed for ${REDACTED_EMAIL}`);
    expect(event.exception.values[0].stacktrace!.frames[0].in_app).toBe(false); // node_modules
  });
});
