#!/usr/bin/env bash
# Capture repeatable PostgreSQL EXPLAIN (ANALYZE, BUFFERS) plans for hot 3commerce query paths.
#
# This script is intentionally read-only: it does not create indexes. Use it to gather the
# before/after evidence required by ADR-0033 before adding any service-owned index migration.
#
# Prerequisite: a running dev stack with seeded data, for example:
#   scripts/dev-up.sh --with-frontends --data dummy
#
# Usage:
#   scripts/postgres-index-audit.sh                 # run all query probes
#   scripts/postgres-index-audit.sh catalog-search  # run one probe
#   scripts/postgres-index-audit.sh --list          # list probe names
set -euo pipefail
cd "$(dirname "$0")/.."

POSTGRES_CONTAINER="${POSTGRES_CONTAINER:-3commerce-postgres}"
PSQL=(docker exec -i "$POSTGRES_CONTAINER" psql -v ON_ERROR_STOP=1 -q)

usage() {
  sed -n '1,18p' "$0" | sed 's/^# \{0,1\}//'
}

require_postgres() {
  if ! docker inspect "$POSTGRES_CONTAINER" >/dev/null 2>&1; then
    echo "Postgres container '$POSTGRES_CONTAINER' is not running." >&2
    echo "Start a seeded stack first: scripts/dev-up.sh --with-frontends --data dummy" >&2
    exit 1
  fi
}

run_sql() {
  local db="$1" user="$2" title="$3" sql="$4"
  printf '\n== %s (%s as %s) ==\n' "$title" "$db" "$user"
  printf '%s\n' "$sql" | "${PSQL[@]}" -U "$user" -d "$db"
}

catalog_search() {
  run_sql catalog_db catalog_svc "catalog-search: storefront keyword search" '
SET search_path TO catalog, public;
EXPLAIN (ANALYZE, BUFFERS)
SELECT "Id", "Slug", "Title", "Brand"
FROM catalog."Products"
WHERE search_vector @@ plainto_tsquery($$english$$, $$wireless speaker$$)
ORDER BY ts_rank_cd(search_vector, plainto_tsquery($$english$$, $$wireless speaker$$)) DESC
LIMIT 20;
'
}

catalog_publication() {
  run_sql catalog_db catalog_svc "catalog-publication: active storefront publications" '
SET search_path TO catalog, public;
EXPLAIN (ANALYZE, BUFFERS)
SELECT "ProductId", "StorefrontId", "SlugOverride", "PublishedAt"
FROM catalog."ProductPublications"
WHERE "TenantId" = $$00000000-0000-0000-0000-000000000001$$
  AND "StorefrontId" = $$00000000-0000-0000-0000-000000000101$$
  AND "State" = 2
ORDER BY "PublishedAt" DESC NULLS LAST
LIMIT 50;
'
}

ordering_cart() {
  run_sql ordering_db ordering_svc "ordering-cart: active cart lookup" '
SET search_path TO ordering, public;
EXPLAIN (ANALYZE, BUFFERS)
SELECT "Id", "CartKey", "UserId", "UpdatedAt"
FROM ordering."Carts"
WHERE "CartKey" = $$anon:index-audit-anonymous-cart$$
LIMIT 1;
'
}

ordering_admin_orders() {
  run_sql ordering_db ordering_svc "ordering-admin-orders: recent orders list" '
SET search_path TO ordering, public;
EXPLAIN (ANALYZE, BUFFERS)
SELECT "Id", "PublicOrderNumber", "Status", "Email", "GrossMinor", "CreatedAt"
FROM ordering."Orders"
WHERE "TenantId" = $$00000000-0000-0000-0000-000000000001$$
ORDER BY "CreatedAt" DESC
LIMIT 50;
'
}

payments_admin_refunds() {
  run_sql payments_db payments_svc "payments-admin-refunds: recent refunds" '
SET search_path TO payments, public;
EXPLAIN (ANALYZE, BUFFERS)
SELECT "Id", "OrderId", "AmountMinor", "Status", "CreatedAt"
FROM payments."Refunds"
ORDER BY "CreatedAt" DESC
LIMIT 50;
'
}

fulfillment_admin_shipments() {
  run_sql fulfillment_db fulfillment_svc "fulfillment-admin-shipments: open shipments" '
SET search_path TO fulfillment, public;
EXPLAIN (ANALYZE, BUFFERS)
SELECT "Id", "OrderId", "Status", "TrackingNumber", "CreatedAt"
FROM fulfillment."Shipments"
WHERE "TenantId" = $$00000000-0000-0000-0000-000000000001$$
  AND "Status" <> 2
ORDER BY "CreatedAt" DESC
LIMIT 50;
'
}

usage_balances() {
  run_sql usage_db usage_svc "usage-balances: customer usage balances" '
SET search_path TO usage, public;
EXPLAIN (ANALYZE, BUFFERS)
SELECT "Id", "CustomerEmail", "Meter", "UsedQuantity", "IncludedQuantity", "PeriodStart", "PeriodEnd"
FROM usage."UsageBalances"
WHERE "TenantId" = $$00000000-0000-0000-0000-000000000001$$
  AND "CustomerEmail" = $$index-audit-customer@example.com$$
ORDER BY "PeriodStart" DESC;
'
}

list_probes() {
  cat <<'PROBES'
catalog-search
catalog-publication
ordering-cart
ordering-admin-orders
payments-admin-refunds
fulfillment-admin-shipments
usage-balances
PROBES
}

run_probe() {
  case "$1" in
    catalog-search) catalog_search ;;
    catalog-publication) catalog_publication ;;
    ordering-cart) ordering_cart ;;
    ordering-admin-orders) ordering_admin_orders ;;
    payments-admin-refunds) payments_admin_refunds ;;
    fulfillment-admin-shipments) fulfillment_admin_shipments ;;
    usage-balances) usage_balances ;;
    *) echo "Unknown probe '$1'. Use --list." >&2; exit 2 ;;
  esac
}

case "${1:-}" in
  -h|--help) usage; exit 0 ;;
  --list) list_probes; exit 0 ;;
  "")
    require_postgres
    while IFS= read -r probe; do run_probe "$probe"; done < <(list_probes)
    ;;
  *)
    require_postgres
    run_probe "$1"
    ;;
esac
