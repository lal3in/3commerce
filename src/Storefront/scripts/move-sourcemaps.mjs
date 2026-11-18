// postbuild (npm run build): move every browser source map out of the SERVED directory.
//
// next.config.ts builds them (productionBrowserSourceMaps) so a Faro stack trace can be symbolicated, but they must
// never be public — they are the readable source. Everything under <distDir>/static is served at /_next/static and
// copied into the container image, so the maps go to <distDir>/sourcemaps/<same relative path> instead: a build
// artifact for the upload step (docs/help/getting-started.md, "Storefront source maps"). middleware.ts 404s any
// /_next/static/**.map request in production as a second guard.

import { mkdirSync, readdirSync, renameSync, statSync } from "node:fs";
import path from "node:path";

const distDir = process.env.NEXT_DIST_DIR || ".next";
const staticDir = path.join(distDir, "static");
const targetDir = path.join(distDir, "sourcemaps");

function walk(dir) {
  let entries = [];
  try {
    entries = readdirSync(dir);
  } catch {
    return [];
  }
  return entries.flatMap((name) => {
    const full = path.join(dir, name);
    return statSync(full).isDirectory() ? walk(full) : [full];
  });
}

const maps = walk(staticDir).filter((file) => file.endsWith(".map"));
for (const file of maps) {
  const dest = path.join(targetDir, path.relative(staticDir, file));
  mkdirSync(path.dirname(dest), { recursive: true });
  renameSync(file, dest);
}
console.log(`move-sourcemaps: ${maps.length} browser source map(s) moved ${staticDir} -> ${targetDir} (not served)`);
