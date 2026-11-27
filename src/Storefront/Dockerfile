# syntax=docker/dockerfile:1
# Build context must be the repo root (matches the service Dockerfiles):
#   docker build -f src/Storefront/Dockerfile .
# Produces a slim runner from Next.js standalone output (next.config: output "standalone").
#
# Package manager: pnpm (ADR-0070), at exactly the version package.json "packageManager" pins — read from the
# manifest so the pin has one source. node:22 has no usable corepack for pnpm 12, so npm installs it once here.
FROM node:22-alpine AS pnpm
WORKDIR /app
COPY src/Storefront/package.json ./
RUN npm install -g --no-fund --no-audit "$(node -p 'require("./package.json").packageManager')"

FROM pnpm AS deps
COPY src/Storefront/package.json src/Storefront/pnpm-lock.yaml src/Storefront/pnpm-workspace.yaml ./
# The store lives in a cache mount (reused across builds, never in a layer); node_modules is COPIED out of it
# (the mount is another filesystem, so pnpm cannot hard-link), so the layer holds real files.
RUN --mount=type=cache,target=/pnpm/store,sharing=locked \
    pnpm install --frozen-lockfile --store-dir /pnpm/store

FROM pnpm AS build
COPY --from=deps /app/node_modules ./node_modules
COPY src/Storefront/ ./
ENV NEXT_TELEMETRY_DISABLED=1
# Release identity (ADR-0064) stamped into the bundle by next.config.ts (NEXT_PUBLIC_SERVICE_VERSION /
# NEXT_PUBLIC_VCS_REVISION): the build has no .git, so it comes from the build args. Declared after the COPY so a
# new SHA only re-runs the build step, which the new sources re-run anyway.
ARG GIT_SHA=""
ARG VERSION=""
# The postbuild step moves browser source maps to .next/sourcemaps; only .next/static is copied below, so they
# never reach the runtime image (upload them from the build stage — docs/help/getting-started.md).
RUN GIT_SHA="$GIT_SHA" VERSION="$VERSION" pnpm build

FROM node:22-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production
ENV NEXT_TELEMETRY_DISABLED=1
ENV PORT=3000
# Next standalone server.js binds to $HOSTNAME, which Docker sets to the container ID
# (so it would listen only on the eth0 IP — loopback healthchecks then fail). Bind to all
# interfaces so the container's own `wget http://localhost:3000/` healthcheck succeeds.
ENV HOSTNAME=0.0.0.0
# Run as the non-root user shipped in the node image.
USER node
COPY --from=build --chown=node:node /app/public ./public
# pnpm's standalone output keeps node_modules/<pkg> as RELATIVE symlinks into node_modules/.pnpm/ inside the
# standalone tree; COPY preserves them, so they resolve in this stage too.
COPY --from=build --chown=node:node /app/.next/standalone ./
COPY --from=build --chown=node:node /app/.next/static ./.next/static
EXPOSE 3000
# OCI image labels (ADR-0064): which commit this image is. Last, so a new SHA never busts the layers above.
ARG GIT_SHA=""
ARG VERSION=""
LABEL org.opencontainers.image.source="https://github.com/lal3in/3commerce" \
      org.opencontainers.image.revision="$GIT_SHA" \
      org.opencontainers.image.version="$VERSION"
CMD ["node", "server.js"]
