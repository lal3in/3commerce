import { resolveEnvironment } from "./release";

/**
 * The deliberate-error probes (/probe/error, /probe/client-error) exist so the E2E suite can prove the error pages
 * and their support reference end to end. They answer 404 in production (deployment.environment.name = prod, the
 * default for a production server with nothing explicit — lib/telemetry/release.ts) so nobody can fill the prod
 * error telemetry with them.
 */
export function probesEnabled(): boolean {
  return resolveEnvironment() !== "prod";
}
