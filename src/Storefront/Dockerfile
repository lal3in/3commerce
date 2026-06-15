# Build context must be the repo root (matches the service Dockerfiles):
#   docker build -f src/Storefront/Dockerfile .
# Produces a slim runner from Next.js standalone output (next.config: output "standalone").
FROM node:22-alpine AS deps
WORKDIR /app
COPY src/Storefront/package.json src/Storefront/package-lock.json ./
RUN npm ci

FROM node:22-alpine AS build
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY src/Storefront/ ./
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm run build

FROM node:22-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production
ENV NEXT_TELEMETRY_DISABLED=1
ENV PORT=3000
# Run as the non-root user shipped in the node image.
USER node
COPY --from=build --chown=node:node /app/public ./public
COPY --from=build --chown=node:node /app/.next/standalone ./
COPY --from=build --chown=node:node /app/.next/static ./.next/static
EXPOSE 3000
CMD ["node", "server.js"]
