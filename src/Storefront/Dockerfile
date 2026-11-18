# syntax=docker/dockerfile:1
# Build context must be the repo root (matches the service Dockerfiles):
#   docker build -f src/Storefront/Dockerfile .
# Produces a slim runner from Next.js standalone output (next.config: output "standalone").
FROM node:22-alpine AS deps
WORKDIR /app
COPY src/Storefront/package.json src/Storefront/package-lock.json ./
RUN --mount=type=cache,target=/root/.npm,sharing=locked npm ci

FROM node:22-alpine AS build
WORKDIR /app
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
RUN GIT_SHA="$GIT_SHA" VERSION="$VERSION" npm run build

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
