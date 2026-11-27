# 0070 — Storefront package manager: pnpm

Status: Accepted — implemented
Area: Storefront / build tooling / supply chain
Decided: owner decision 2026-11-27
Touches: [0021](./0021-containerized-launch.md) (the storefront image), [0058](./0058-branching-and-release-promotion.md) (the `browser-e2e`, `docker`, `compose-smoke` and `kind-deploy` gates install the storefront)

## Context

The Next.js storefront (`src/Storefront`) was installed with npm from a `package-lock.json`. That caused trouble
in two ways:

- **The lockfile depended on which npm wrote it.** CI and the Dockerfile run Node 22 (npm 10), and a lock written
  by a newer local npm left out entries that npm 10's `npm ci` requires. That turned `browser-e2e`, `docker` and
  `kind-deploy` red together (#346), and the workaround was to regenerate the lock with `npx -y npm@10`.
- **npm's flat `node_modules` hides phantom dependencies.** Code can import any package that happens to be
  hoisted (`scripts/fe-audit.mjs` and `fe-flow.mjs` loaded `playwright`, which only comes in through
  `@playwright/test`). Nothing checked that a dependency was declared until a hoisting change broke it.

The owner chose pnpm on 2026-11-27.

## Decision

1. **pnpm is the only package manager for the storefront**, pinned exactly by `"packageManager": "pnpm@12.23.0"`
   in `src/Storefront/package.json`. That field is the only place the version is written:
   - CI uses `pnpm/action-setup@v6` with `package_json_file: src/Storefront/package.json`.
   - The Dockerfile's `pnpm` base stage reads the pin from the manifest (`npm install -g "$(node -p …packageManager)"`).
   - Developers run `npm install -g pnpm@<pin>`, or `corepack enable pnpm` where corepack still ships (it is gone
     from Node 25).
   - `scripts/lib/preflight.sh` `require_storefront_deps` stops with that hint when pnpm is missing.
2. **The lock is `src/Storefront/pnpm-lock.yaml`.** It was converted with `pnpm import` from the npm lock, which
   kept every resolved version (next 15.5.29, postcss 8.5.33, @playwright/test 1.61.1, sharp 0.35.5). Every
   install uses `pnpm install --frozen-lockfile`: CI, the image, `dev-up.sh --with-frontends` (when
   `node_modules` is missing) and `e2e-verify.sh`.
3. **pnpm settings live in `src/Storefront/pnpm-workspace.yaml`**, not in a `"pnpm"` field of `package.json`.
   pnpm 10+ ignores that field, and pnpm 12 fails on unknown keys when the pin matches.
   - `overrides`: `next>postcss: ^8.5.33` (#358's npm `overrides`, same effect).
   - `allowBuilds`: no dependency script runs unless it is listed here. `@parcel/watcher`, `@swc/core`,
     `@tailwindcss/oxide` and `unrs-resolver` are listed as `false`, because each one ships prebuilt binaries
     per os/cpu/libc as optional dependencies (linux-musl included for the alpine image). Their scripts are only
     fallbacks: build from source, download a binary, check for wasm. sharp has no install script.
4. **The default strict (isolated) `node_modules`.** No `shamefully-hoist` and no `nodeLinker: hoisted`.
   - Next's `output: "standalone"` works with it: the traced `.next/standalone/node_modules` keeps relative
     symlinks into its own `.pnpm/`, and Docker `COPY` keeps them.
   - The one phantom import, `playwright` in `fe-*.mjs`, now requires `@playwright/test`. That is a direct
     dependency and re-exports `chromium`, so no new dependency is needed.
5. **The audit gate stays production-only at `high`, now run as `pnpm audit --prod --audit-level high`.** It runs
   in CI step "pnpm vulnerable package scan (production deps)" and in `e2e-verify.sh` A8b.
6. **`.dockerignore` excludes every `node_modules` and the storefront build output.** Before, it excluded only a
   root `node_modules`, so `COPY src/Storefront/ ./` could copy a developer's host `node_modules` (a pnpm
   symlink tree for the host platform) over the image's own install.

## Consequences

- Contributors need pnpm at the pinned version. To bump it, change `packageManager` and nothing else; the pnpm
  version recorded in the lockfile follows on the next `pnpm install`.
- Adding a dependency means `pnpm add <pkg>` and committing `pnpm-lock.yaml`. A new dependency with an install
  script fails the install (`ERR_PNPM_IGNORED_BUILDS`) until someone decides on it in `allowBuilds`, which
  makes it an explicit supply-chain review step. Code that imports a transitive package fails with
  `MODULE_NOT_FOUND` until the package is declared.
- The npm-10 lockfile gotcha no longer applies. `docs/reference/engineering-gotchas.md` now carries the pnpm
  equivalent: a stale lock fails `--frozen-lockfile` everywhere at once, and settings go in `pnpm-workspace.yaml`.
- Image: the runtime is the same standalone server. It measured 325 MB on disk / 79.4 MB content against 353 MB /
  88.2 MB for the npm build of develop 592f0e63, and the build stages keep the pnpm store in a cache mount.
- CI `browser-e2e`: `pnpm install --frozen-lockfile` took 3.5 s on the first run (cold pnpm cache) against
  13 s for `npm ci`, and the whole install step (with the Playwright browser) dropped from 35 s to 23 s.
- Rejected:
  - **Staying on npm with an `engines.npm` pin.** It fixes neither the phantom dependencies nor the cost of
    `npm ci`.
  - **Yarn Berry PnP.** Next standalone tracing and Playwright need `node_modules` workarounds under PnP.
  - **`nodeLinker: hoisted`.** It would give up the strictness this ADR is for, and standalone does not need it.
