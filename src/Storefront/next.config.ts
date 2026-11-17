import { execSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";
import { ALLOWED_IMAGE_HOSTS } from "./lib/image-hosts";

/**
 * Release identity stamped into the bundle (ADR-0064; read back by lib/telemetry/release.ts). Same sources and
 * shape as the .NET builds: GIT_SHA / VERSION from the build (scripts/build-images.sh, compose `x-release-args`,
 * CI pass them as Docker build args), else git for a local build, else nothing — never a failed build.
 */
function releaseIdentity(): { serviceVersion: string; revision: string } {
  const git = (args: string): string => {
    try {
      return execSync(`git ${args}`, { cwd: __dirname, stdio: ["ignore", "pipe", "ignore"] }).toString().trim();
    } catch {
      return "";
    }
  };
  let sha = (process.env.GIT_SHA ?? "").trim();
  if (!sha) {
    sha = git("rev-parse HEAD");
    if (sha && git("status --porcelain --untracked-files=no")) sha += "-dirty";
  }
  let version = (process.env.VERSION ?? "").trim();
  if (!version) {
    try {
      const props = readFileSync(path.join(__dirname, "..", "..", "Directory.Build.props"), "utf8");
      version = /<VersionPrefix>([^<]+)<\/VersionPrefix>/.exec(props)?.[1]?.trim() ?? "";
    } catch {
      /* container build: the props file is outside the build context */
    }
  }
  version ||= "1.0.0";
  const dirty = sha.toLowerCase().endsWith("-dirty");
  const revision = dirty ? sha.slice(0, -"-dirty".length).toLowerCase() : sha.toLowerCase();
  if (!/^[0-9a-f]{7,40}$/.test(revision)) return { serviceVersion: version, revision: "" };
  return { serviceVersion: `${version}+${revision.slice(0, 7)}${dirty ? "-dirty" : ""}`, revision };
}

const release = releaseIdentity();

const nextConfig: NextConfig = {
  // Build output directory. Defaults to .next; the regression suite (scripts/e2e-verify.sh) builds into
  // .next-verify instead, because `next build` rewrites its output dir wholesale — sharing .next with a
  // running `next dev` pulled chunks out from under the dev server ("Cannot find module
  // './vendor-chunks/@formatjs.js'") and every page 500'd until .next was deleted.
  distDir: process.env.NEXT_DIST_DIR || ".next",
  // Self-contained server bundle for a slim production container (BL-10).
  output: "standalone",
  // Optimize seed/demo image hosts (see lib/image-hosts). Unknown hosts don't 500 the page —
  // SafeImage degrades them to a plain <img> — but only these are run through the optimizer.
  images: {
    remotePatterns: ALLOWED_IMAGE_HOSTS.map((hostname) => ({ protocol: "https" as const, hostname })),
  },
  // Browser source maps are BUILT (so a Faro stack trace can be symbolicated) but never SERVED: the postbuild
  // step (scripts/move-sourcemaps.mjs) moves every .map out of <distDir>/static into <distDir>/sourcemaps for
  // upload, and middleware.ts answers /_next/static/**.map with 404 in production as a second guard.
  // Upload step: docs/help/observability.md ("Storefront source maps").
  productionBrowserSourceMaps: true,
  env: {
    NEXT_PUBLIC_SERVICE_VERSION: release.serviceVersion,
    NEXT_PUBLIC_VCS_REVISION: release.revision,
  },
  experimental: {
    // Server-rendered pages carry <meta name="traceparent"> for the SSR trace, so an error page rendered in
    // the browser can show the trace id as its support reference (app/error.tsx).
    clientTraceMetadata: ["traceparent"],
  },
};

// i18n_1: no locale URL segment — the request locale comes from the session cookie / storefront
// default / Accept-Language (i18n/request.ts), so /au, /products/... keep their existing shape.
export default createNextIntlPlugin("./i18n/request.ts")(nextConfig);
