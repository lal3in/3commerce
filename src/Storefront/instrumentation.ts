// Next.js SSR tracing (audit P1-3) with @vercel/otel.
//
// - Next 15 emits its own spans for every render, route handler and server `fetch` once a tracer provider is
//   registered; @vercel/otel registers it and exports over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT (or the
//   _TRACES_ variant) is set — http/protobuf by default, so point it at the collector's :4318 (dev-up does).
//   Without an endpoint nothing is exported and nothing fails.
// - `fetch` to the gateway carries `traceparent`, so a storefront render and the gateway → services → bus spans it
//   causes are ONE trace. Context is propagated to the gateway only, never to third parties.
// - Resource = the release identity of ADR-0064: service.name, service.version (<Version>+<sha7>[-dirty]),
//   vcs.ref.head.revision, deployment.environment.name, service.instance.id (one per process).
// - onRequestError logs every server-side render/route error with its digest AND trace id: the digest is what
//   app/error.tsx shows the shopper as the support reference, so support can go digest → log line → trace.

import type { Instrumentation } from "next";

export async function register() {
  if (process.env.NEXT_RUNTIME !== "nodejs") return;
  const { registerOTel } = await import("@vercel/otel");
  const { randomUUID } = await import("node:crypto");
  const { ENVIRONMENT_KEY, SERVICE_NAME, SERVICE_NAMESPACE, resolveEnvironment, serviceVersion, vcsRevision } =
    await import("./lib/telemetry/release");

  const gateway = (process.env.GATEWAY_URL ?? "http://localhost:8080").replace(/\/$/, "");
  const revision = vcsRevision();
  const environment = resolveEnvironment();
  const attributes: Record<string, string> = {
    "service.namespace": SERVICE_NAMESPACE,
    "service.version": serviceVersion(),
    [ENVIRONMENT_KEY]: environment,
    "service.instance.id": randomUUID(),
  };
  if (revision) attributes["vcs.ref.head.revision"] = revision;

  registerOTel({
    serviceName: process.env.OTEL_SERVICE_NAME || SERVICE_NAME,
    attributes,
    instrumentationConfig: {
      fetch: {
        propagateContextUrls: [gateway],
        // The RUM relay's hop to the collector is not traced (app/rum/route.ts also opts out per call).
        ignoreUrls: [/:8027(\/|$)/],
      },
    },
  });

  console.info(
    `Release identity: service.name=${SERVICE_NAME} service.version=${attributes["service.version"]} ` +
      `vcs.ref.head.revision=${revision ?? ""} ${ENVIRONMENT_KEY}=${environment} ` +
      `service.instance.id=${attributes["service.instance.id"]}`,
  );
}

export const onRequestError: Instrumentation.onRequestError = async (error, request, context) => {
  if (process.env.NEXT_RUNTIME !== "nodejs") return;
  const { trace, SpanStatusCode } = await import("@opentelemetry/api");
  const { scrubText, scrubUrl } = await import("./lib/telemetry/scrub");
  const err = error as Error & { digest?: string };
  const span = trace.getActiveSpan();
  if (span) {
    span.recordException(err);
    span.setStatus({ code: SpanStatusCode.ERROR, message: scrubText(err.message ?? "") });
  }
  // One structured line per server error (.run/storefront.log, container stdout). No query string, no cookies.
  console.error(
    JSON.stringify({
      level: "error",
      msg: "storefront request error",
      digest: err.digest ?? null,
      traceId: span?.spanContext().traceId ?? null,
      method: request.method,
      path: scrubUrl(request.path),
      routePath: context.routePath,
      routeType: context.routeType,
      renderSource: (context as { renderSource?: string }).renderSource ?? null,
      error: { name: err.name, message: scrubText(err.message ?? "") },
    }),
  );
};
