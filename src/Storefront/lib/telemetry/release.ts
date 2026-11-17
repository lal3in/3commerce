// Release identity of the storefront (ADR-0064), shared by SSR tracing (instrumentation.ts) and browser RUM
// (lib/telemetry/faro.ts).
//
// - service.version / vcs.ref.head.revision are BUILD facts: next.config.ts stamps them into the bundle as
//   NEXT_PUBLIC_SERVICE_VERSION / NEXT_PUBLIC_VCS_REVISION (from GIT_SHA/VERSION, as the .NET builds do, or from
//   git for a local build). Same `<Version>+<sha7>[-dirty]` shape as ReleaseIdentity.cs.
// - deployment.environment.name is a RUNTIME fact: one image is promoted dev → test → prod (ADR-0058), so it is
//   read from the server's environment on each render and handed to the browser as a prop, never inlined.

export const SERVICE_NAME = "storefront";
export const SERVICE_NAMESPACE = "3commerce";
export const ENVIRONMENT_KEY = "deployment.environment.name";

/** `<Version>+<sha7>[-dirty]`, inlined at build time. "0.0.0" only for a build that carried no metadata at all. */
export function serviceVersion(): string {
  return process.env.NEXT_PUBLIC_SERVICE_VERSION || "0.0.0";
}

/** The full commit SHA the bundle was built from, or undefined (never a crash — ADR-0064). */
export function vcsRevision(): string | undefined {
  return process.env.NEXT_PUBLIC_VCS_REVISION || undefined;
}

/** Parses `OTEL_RESOURCE_ATTRIBUTES` (`k=v,k2=v2`, values percent-encoded per the OTel spec). */
export function parseResourceAttributes(raw: string | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  if (!raw) return out;
  for (const pair of raw.split(",")) {
    const eq = pair.indexOf("=");
    if (eq <= 0) continue;
    const key = pair.slice(0, eq).trim();
    let value = pair.slice(eq + 1).trim();
    try {
      value = decodeURIComponent(value);
    } catch {
      /* keep the raw value */
    }
    if (key) out[key] = value;
  }
  return out;
}

/** Host environment name → dev / test / prod / ci (else lower-cased), the same map as ReleaseIdentity.MapEnvironment. */
export function mapEnvironment(name: string | undefined): string | undefined {
  const n = name?.trim().toLowerCase();
  if (!n) return undefined;
  if (n === "development" || n === "dev") return "dev";
  if (n === "staging" || n === "test") return "test";
  if (n === "production" || n === "prod") return "prod";
  return n;
}

/**
 * deployment.environment.name for this process. An explicit OTEL_RESOURCE_ATTRIBUTES entry always wins (CI sets
 * `ci`, `launch.sh --env prod` sets `prod`), then DEPLOYMENT_ENVIRONMENT, then NODE_ENV (`next dev` → dev; a
 * production server with nothing explicit is treated as prod — the safe default for anything gated on it).
 */
export function resolveEnvironment(env: Record<string, string | undefined> = process.env): string {
  return (
    mapEnvironment(parseResourceAttributes(env.OTEL_RESOURCE_ATTRIBUTES)[ENVIRONMENT_KEY]) ??
    mapEnvironment(env.DEPLOYMENT_ENVIRONMENT) ??
    mapEnvironment(env.NODE_ENV) ??
    "prod"
  );
}
