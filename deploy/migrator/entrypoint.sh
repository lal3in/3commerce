#!/bin/sh
# Applies each service's EF migration bundle against its database. Connection strings are
# injected per service (compose/Helm); each DB already exists via Postgres init SQL.
set -eu

migrate() {
  svc="$1"
  conn="$2"
  if [ -z "$conn" ]; then
    echo "ERROR: no connection string for '$svc' (set CONN_$(echo "$svc" | tr '[:lower:]' '[:upper:]'))" >&2
    exit 1
  fi
  echo ">> migrating $svc"
  "/bundles/efbundle-$svc" --connection "$conn"
}

migrate identity    "${CONN_IDENTITY:-}"
migrate catalog     "${CONN_CATALOG:-}"
migrate entity      "${CONN_ENTITY:-}"
migrate ordering    "${CONN_ORDERING:-}"
migrate payments    "${CONN_PAYMENTS:-}"
migrate fulfillment "${CONN_FULFILLMENT:-}"
migrate support     "${CONN_SUPPORT:-}"
migrate marketing   "${CONN_MARKETING:-}"
migrate pricing   "${CONN_PRICING:-}"
migrate audit   "${CONN_AUDIT:-}"
migrate workflow   "${CONN_WORKFLOW:-}"
migrate entitlement   "${CONN_ENTITLEMENT:-}"

echo ">> all migrations applied"
