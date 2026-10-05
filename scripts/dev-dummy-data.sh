#!/usr/bin/env bash
# Seed a running DEV stack with broad, realistic demo data through public/admin APIs.
#
# Usage:
#   scripts/dev-dummy-data.sh [--gateway http://localhost:8080] [--profile smoke|core|full|exhaustive|mirror-prod]
#
# Profiles:
#   smoke      fast deterministic data for browser smoke tests (catalog import + stable customer)
#   core       alias for smoke, kept for backwards compatibility
#   full       smoke + best-effort tenant/operator data across Entity, Marketing, Pricing,
#              Payments, Fulfillment, Entitlement, and Usage admin APIs
#   exhaustive full + placeholder hook for slower historical scenarios as they are implemented
#   mirror-prod reserved hook for a future prod-snapshot mirroring flow; intentionally no-op now
#
# This script is intentionally API-first. It does not write service databases directly, so
# invariants, RLS, outbox, audit, and validation stay in the owning services.
#
# Orders are placed only on the seed's own LIVE demo storefronts, for products sellable there (published,
# priced in the store's currency, approved supply), shipped to a country the store serves, from an empty
# cart. Exit status: 0 = seeded; 1 = a prerequisite failed (admin login, catalog import); 4 = everything was
# seeded but a REQUIRED step (cart add, checkout, shopper login) failed — each is printed with its body.
set -euo pipefail
cd "$(dirname "$0")/.."

GATEWAY="${GATEWAY:-http://localhost:8080}"
PROFILE="full"
TENANT_ID="${TENANT_ID:-00000000-0000-0000-0000-000000000001}"
ADMIN_EMAIL="${ADMIN_EMAIL:-admin@3commerce.local}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-dev-admin-password-1}"
OUT_DIR="${OUT_DIR:-.run/dev-dummy-data}"
RUN_ID="${RUN_ID:-$(date +%Y%m%d%H%M%S)}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --gateway) GATEWAY="$2"; shift 2 ;;
    --profile) PROFILE="$2"; shift 2 ;;
    --tenant-id) TENANT_ID="$2"; shift 2 ;;
    --admin-email) ADMIN_EMAIL="$2"; shift 2 ;;
    --admin-password) ADMIN_PASSWORD="$2"; shift 2 ;;
    --out-dir) OUT_DIR="$2"; shift 2 ;;
    --run-id) RUN_ID="$2"; shift 2 ;;
    -h|--help) sed -n '2,21p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

case "$PROFILE" in
  core) PROFILE="smoke" ;;
  smoke|full|exhaustive|mirror-prod) ;;
  *) echo "Unknown profile: $PROFILE (expected smoke|core|full|exhaustive|mirror-prod)" >&2; exit 2 ;;
esac

mkdir -p "$OUT_DIR"
ADMIN_JAR="$OUT_DIR/admin.cookie"
CUSTOMER_JAR="$OUT_DIR/customer.cookie"
SUPPLIER_JAR="$OUT_DIR/supplier.cookie"
SUMMARY="$OUT_DIR/summary.jsonl"
MANIFEST="$OUT_DIR/fixtures.json"
: > "$SUMMARY"

need() { command -v "$1" >/dev/null 2>&1 || { echo "Missing required command: $1" >&2; exit 1; }; }
need curl
need python3

SCENARIO_CODES=(
  physical-warehouse-flat
  physical-dropship-flat
  physical-multi-variant-tiered
  bundle-mixed-physical
  digital-download-onetime
  subscription-monthly-flat
  subscription-yearly-tiered
  usage-api-meter
  manual-service-onetime
  out-of-stock-hold
  inactive-unpublished-private
)

init_manifest() {
  python3 - "$MANIFEST" "$PROFILE" "$RUN_ID" "$GATEWAY" "$TENANT_ID" <<'PY'
import json, sys
path, profile, run_id, gateway, tenant_id = sys.argv[1:]
data = {
    "schemaVersion": 1,
    "profile": profile,
    "runId": run_id,
    "gateway": gateway,
    "tenantId": tenant_id,
    "scenarioCodes": [],
    "customers": {},
    "admin": {},
    "entities": {},
    "products": {},
    "offers": {},
    "orders": {},
    "payments": {},
    "fulfillment": {},
    "support": {},
    "subscriptions": {},
    "usage": {},
    "warnings": [],
}
open(path, 'w', encoding='utf-8').write(json.dumps(data, indent=2, sort_keys=True) + '\n')
PY
}

manifest_set() {
  local dotted_path="$1" value_json="$2"
  python3 - "$MANIFEST" "$dotted_path" "$value_json" <<'PY'
import json, sys
path, dotted, raw = sys.argv[1:]
with open(path, encoding='utf-8') as f:
    data = json.load(f)
value = json.loads(raw)
cur = data
parts = [p for p in dotted.split('.') if p]
for part in parts[:-1]:
    cur = cur.setdefault(part, {})
cur[parts[-1]] = value
open(path, 'w', encoding='utf-8').write(json.dumps(data, indent=2, sort_keys=True) + '\n')
PY
}

manifest_append() {
  local dotted_path="$1" value_json="$2"
  python3 - "$MANIFEST" "$dotted_path" "$value_json" <<'PY'
import json, sys
path, dotted, raw = sys.argv[1:]
with open(path, encoding='utf-8') as f:
    data = json.load(f)
value = json.loads(raw)
cur = data
parts = [p for p in dotted.split('.') if p]
for part in parts[:-1]:
    cur = cur.setdefault(part, {})
cur.setdefault(parts[-1], []).append(value)
open(path, 'w', encoding='utf-8').write(json.dumps(data, indent=2, sort_keys=True) + '\n')
PY
}

json_string() { python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$1"; }

json_get() {
  # NB: the program must go via -c, NOT a heredoc — `python3 - <<EOF` makes the heredoc python's
  # stdin, so json.load(sys.stdin) would read nothing and every lookup silently returned empty
  # (manifest ids were never captured; scenario seeding degraded). Review finding F10 / rev_11.
  python3 -c '
import json, sys
path = sys.argv[1]
try:
    data = json.load(sys.stdin)
    cur = data
    for part in path.split("."):
        if not part:
            continue
        if isinstance(cur, list):
            cur = cur[int(part)]
        else:
            cur = cur[part]
    print(cur)
except Exception:
    pass
' "$1"
}

# Step expectations (the 6th arg of api/record):
#   allow_4xx  best-effort / idempotent step — a 4xx is normal on a re-run (already exists, no-op transition).
#   must_2xx   the step MUST succeed (cart adds + checkouts). A failure is recorded as unexpected_4xx /
#              server_error, printed loudly WITH the response body, and makes the whole seed exit 4 at the
#              end (after every other step has still run) — never folded into allowed_4xx.
#   expect_2xx a prerequisite (admin login, catalog import): a failure aborts the seed immediately.
# Print a failed must_2xx step loudly, with the response body, so the cause is diagnosable from the log.
report_unexpected() {
  local name="$1" method="$2" path="$3" code="$4" body_file="$5"
  {
    echo "  !! UNEXPECTED: $name $method $path -> $code"
    printf '  !! body: %s\n' "$(head -c 600 "$body_file" 2>/dev/null | tr '\n' ' ')"
  } >&2
}

record() {
  local name="$1" method="$2" path="$3" code="$4" body_file="$5" expectation="${6:-allow_4xx}"
  python3 - "$name" "$method" "$path" "$code" "$body_file" "$expectation" >> "$SUMMARY" <<'PY'
import json, sys
name, method, path, code, body_file, expectation = sys.argv[1:]
body = ''
try:
    body = open(body_file, encoding='utf-8').read()[:800]
except Exception:
    pass
status = 'curl_failed'
if code.startswith(('2', '3')):
    status = 'ok'
elif code.startswith('4') and expectation == 'allow_4xx':
    status = 'allowed_4xx'
elif code.startswith('4'):
    status = 'unexpected_4xx'
elif code.startswith('5'):
    status = 'server_error'
print(json.dumps({"step": name, "method": method, "path": path, "status": code, "classification": status, "expectation": expectation, "bodyPreview": body}))
PY
}

api() {
  local name="$1" method="$2" path="$3" jar="$4" data="${5:-}" expectation="${6:-allow_4xx}"
  local body_file="$OUT_DIR/${name//[^A-Za-z0-9_.-]/_}.json"
  local code
  if [[ -n "$data" ]]; then
    code=$(curl -sS -k -b "$jar" -c "$jar" -X "$method" "$GATEWAY$path" \
      -H 'content-type: application/json' -d "$data" -o "$body_file" -w '%{http_code}' || true)
  else
    code=$(curl -sS -k -b "$jar" -c "$jar" -X "$method" "$GATEWAY$path" \
      -o "$body_file" -w '%{http_code}' || true)
  fi
  record "$name" "$method" "$path" "$code" "$body_file" "$expectation"
  printf '  %-40s %s %s -> %s\n' "$name" "$method" "$path" "$code" >&2
  if [[ "$expectation" == "must_2xx" && ! "$code" =~ ^2|^3 ]]; then
    report_unexpected "$name" "$method" "$path" "$code" "$body_file"
  fi
  if [[ "$expectation" == "expect_2xx" && ! "$code" =~ ^2|^3 ]]; then
    echo "Required seed step failed: $name ($code)" >&2
    cat "$body_file" >&2 || true
    exit 1
  fi
  cat "$body_file"
}

api_noauth() {
  local name="$1" method="$2" path="$3" jar="$4" data="${5:-}" expectation="${6:-allow_4xx}"
  api "$name" "$method" "$path" "$jar" "$data" "$expectation"
}

# Idempotency guard for supplier offers. An offer is uniquely identified by the FULL key
# (TenantId, ProductId, VariantId, SupplierId, StorefrontId) — the same tenant/supplier may
# legitimately hold DIFFERENT offers per storefront (per-storefront pricing) and different suppliers
# may cover the same product (multi-supplier). A true duplicate is that exact key appearing twice, and
# the create endpoint always inserts, so an unguarded re-run — or two seed iterations that hit the same
# key (e.g. multiple stores of one currency) — piled 83 offers onto 33 unique keys for the demo supplier.
# Returns "yes" when an offer for the given key already exists (tenant is fixed to $TENANT_ID; variant
# and storefront are matched null-aware, empty/"null" == unset). Callers skip the POST when it prints yes.
# The list's product filter is `product=` (id OR title, #255). It used to be `productId=`; the stale name
# was silently ignored, so EVERY offer of the supplier came back and the first all-store product-level offer
# made every later product look "already offered" — only one per-store COGS offer was ever created, store
# orders carried no supplier, and the supplier portal had no orders to deliver. The product id is matched
# below as well, so an ignored filter can never collapse the key again.
offer_key_exists() {
  local supplier_id="$1" product_id="$2" variant_id="${3:-}" storefront_id="${4:-}"
  local existing
  existing=$(api "offer-dedupe-${product_id:0:8}" GET \
    "/api/catalog/admin/offers?tenantId=$TENANT_ID&supplierId=$supplier_id&product=$product_id" \
    "$ADMIN_JAR" "" "allow_4xx")
  printf '%s' "$existing" | PRODUCT_ID="$product_id" VARIANT_ID="$variant_id" STOREFRONT_ID="$storefront_id" python3 -c '
import json, os, sys

def norm(v):
    v = (v or "").strip().lower()
    return None if v in ("", "null", "none") else v

want_product = norm(os.environ.get("PRODUCT_ID"))
want_variant = norm(os.environ.get("VARIANT_ID"))
want_storefront = norm(os.environ.get("STOREFRONT_ID"))
try:
    rows = json.load(sys.stdin)
    rows = rows if isinstance(rows, list) else []
    hit = any(
        isinstance(r, dict)
        and norm(r.get("productId")) == want_product
        and norm(r.get("variantId")) == want_variant
        and norm(r.get("storefrontId")) == want_storefront
        for r in rows
    )
    print("yes" if hit else "")
except Exception:
    print("")'
}

# Poll the admin RMA list until <rma_id> reaches <target_state> (RMA saga transitions are eventually
# consistent, so acting immediately can 409). Returns 0 when reached, 1 on timeout.
rma_wait_state() {
  local rid="$1" want="$2" i body st
  for i in $(seq 1 20); do
    body=$(api "rma-wait-${rid:0:8}-$i" GET "/api/support/admin/rmas" "$ADMIN_JAR" "" "allow_4xx")
    st=$(printf '%s' "$body" | python3 -c "import sys,json
try:
    d=json.load(sys.stdin)
    print(next((r.get('state','') for r in d if r.get('id')=='$rid'), ''))
except Exception:
    print('')")
    [[ "$st" == "$want" ]] && return 0
    sleep 1
  done
  return 1
}

upsert_demo_storefront() {
  local key="$1" name="$2" public_url="$3" currency="$4" tax_regime="$5" tax_bps="$6"
  local body result storefront_id existing
  body="{\"tenantId\":\"$TENANT_ID\",\"name\":\"$name\",\"visibility\":4,\"publicUrl\":\"$public_url\",\"currency\":\"$currency\",\"taxRegime\":$tax_regime,\"taxRateBasisPoints\":$tax_bps}"
  result=$(api "catalog-storefront-$key" POST "/api/catalog/admin/storefronts" "$ADMIN_JAR" "$body" "allow_4xx")
  storefront_id=$(printf '%s' "$result" | json_get id)
  if [[ -z "$storefront_id" ]]; then
    existing=$(api "catalog-storefront-$key-list" GET "/api/catalog/admin/storefronts?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx")
    storefront_id=$(python3 - "$name" "$existing" <<'PY'
import json, sys
name, raw = sys.argv[1:]
try:
    data = json.loads(raw)
    for item in data:
        if item.get('name') == name:
            print(item.get('id', ''))
            break
except Exception:
    pass
PY
)
    if [[ -n "$storefront_id" ]]; then
      api "catalog-storefront-$key-update" PUT "/api/catalog/admin/storefronts/$storefront_id" "$ADMIN_JAR" \
        "{\"name\":\"$name\",\"visibility\":4,\"accessPasswordHash\":null,\"publicUrl\":\"$public_url\",\"currency\":\"$currency\",\"taxRegime\":$tax_regime,\"taxRateBasisPoints\":$tax_bps}" "allow_4xx" >/dev/null
    fi
  fi
  if [[ -n "$storefront_id" ]]; then
    manifest_set "storefronts.$key.id" "$(json_string "$storefront_id")"
    # Draft -> Preview so the public storefront-config endpoint (Active/Preview only) resolves it.
    # (Activate needs a canonical domain, which path-slug demo stores sharing one host can't have.)
    # allow_4xx: on re-runs the store is already Preview and the transition is a no-op 4xx.
    api "catalog-storefront-$key-preview" POST "/api/catalog/admin/storefronts/$storefront_id/preview" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
  fi
}


scenario_product_json() {
  local code="$1" category_id="$2"
  python3 - "$code" "$category_id" "$TENANT_ID" <<'PY'
import json, sys
code, category_id, tenant_id = sys.argv[1:]
meta = {
    "physical-warehouse-flat": ("Physical", "Physical", "Warehouse", "Flat", "OneTime", 1599, 25, 1),
    "physical-dropship-flat": ("Physical", "Physical", "Dropship", "Flat", "OneTime", 2499, 0, 1),
    "physical-multi-variant-tiered": ("Physical", "Physical", "Warehouse", "Tiered", "OneTime", 3999, 30, 3),
    "bundle-mixed-physical": ("Bundle", "Physical", "Mixed", "Flat", "OneTime", 5999, 10, 2),
    "digital-download-onetime": ("DigitalDownload", "Digital", "DigitalDownload", "Flat", "OneTime", 1299, 999, 1),
    "subscription-monthly-flat": ("Subscription", "Digital", "DigitalDownload", "Flat", "Subscription", 999, 999, 1),
    "subscription-yearly-tiered": ("Subscription", "Service", "ManualService", "Tiered", "Subscription", 9999, 999, 2),
    "usage-api-meter": ("Usage", "Digital", "Usage", "UsageBased", "UsageBased", 0, 999, 1),
    "manual-service-onetime": ("ManualService", "Service", "ManualService", "Flat", "OneTime", 7500, 999, 1),
    "out-of-stock-hold": ("Physical", "Physical", "Warehouse", "Flat", "OneTime", 1899, 0, 1),
    "inactive-unpublished-private": ("Physical", "Physical", "Warehouse", "Flat", "OneTime", 2199, 0, 1),
}
product_type, supply, fulfilment, pricing, billing, price, stock, variants = meta[code]
variant_payload = []
for i in range(variants):
    suffix = chr(ord('A') + i)
    _phys = product_type in ("Physical", "Bundle")
    variant_payload.append({"id": None, "sku": f"E2E-{code.upper()}-{suffix}", "priceMinor": price + (i * 500), "currency": "EUR", "stockQuantity": stock,
        "weightGrams": (500 + (i * 100)) if _phys else 0, "lengthMm": 200, "widthMm": 150, "heightMm": 100,
        # Package (boxed) dims — required to publish a physical product (physical readiness). Non-physical: 0.
        "packageWeightGrams": (650 + (i * 100)) if _phys else 0, "packageLengthMm": 250 if _phys else 0, "packageWidthMm": 200 if _phys else 0, "packageHeightMm": 150 if _phys else 0})
# ProductStatus enum binds as a NUMBER over HTTP (AGENTS.md invariant): 1=Active, 2=Inactive.
# The inactive-unpublished-private fixture must NOT be publicly discoverable, so seed it Inactive.
status = 2 if code == "inactive-unpublished-private" else 1
payload = {"tenantId": tenant_id, "slug": f"e2e-scenario-{code}", "title": f"E2E Scenario {code}", "brand": "3commerce QA", "description": f"Deterministic QA scenario product for {code}.", "categoryId": category_id, "status": status, "attributes": {"scenarioCode": code, "productType": product_type, "supplyCategory": supply, "fulfilmentType": fulfilment, "pricingModel": pricing, "billingMode": billing, "qaSeed": "true"}, "imageUrls": [f"https://placehold.co/800x600?text={code}"], "variants": variant_payload}
print(json.dumps(payload))
PY
}

scenario_offer_json() {
  local code="$1" product_id="$2" variant_id="$3" supplier_id="$4"
  python3 - "$code" "$product_id" "$variant_id" "$supplier_id" "$TENANT_ID" <<'PY'
import json, sys
code, product_id, variant_id, supplier_id, tenant_id = sys.argv[1:]
meta = {
    "physical-warehouse-flat": ("Physical", "Warehouse", "OneTime", "Once", 1599, []),
    "physical-dropship-flat": ("Physical", "Dropship", "OneTime", "Once", 2499, []),
    "physical-multi-variant-tiered": ("Physical", "Warehouse", "Tiered", "Once", 3999, [{"fromQuantity":1,"unitPriceMinor":3999},{"fromQuantity":5,"unitPriceMinor":3499},{"fromQuantity":10,"unitPriceMinor":2999}]),
    "bundle-mixed-physical": ("Physical", "Warehouse", "OneTime", "Once", 5999, []),
    "digital-download-onetime": ("Digital", "DigitalDownload", "OneTime", "Once", 1299, []),
    "subscription-monthly-flat": ("Digital", "DigitalDownload", "Subscription", "Monthly", 999, []),
    "subscription-yearly-tiered": ("Service", "ManualService", "Subscription", "Yearly", 9999, []),
    "usage-api-meter": ("Digital", "Usage", "UsageBased", "Monthly", 0, []),
    "manual-service-onetime": ("Service", "ManualService", "OneTime", "Once", 7500, []),
    "out-of-stock-hold": ("Physical", "Warehouse", "OneTime", "Once", 1899, []),
    "inactive-unpublished-private": ("Physical", "Warehouse", "OneTime", "Once", 2199, []),
}
supply, fulfilment, pricing, period, price, tiers = meta[code]
# Enums bind as NUMBERS over HTTP (AGENTS.md invariant).
SUPPLY = {"Physical": 1, "Digital": 2, "Service": 3}
FULFIL = {"Unassigned": 0, "Dropship": 1, "Warehouse": 2, "DigitalDownload": 3, "Subscription": 4, "Usage": 5, "ManualService": 6}
PRICING = {"OneTime": 1, "Subscription": 2, "UsageBased": 3, "Tiered": 4}
PERIOD = {"Once": 1, "Monthly": 2, "Yearly": 3}
# Supplier cost (COGS) is ~half the selling price (price/2 convention, matching PricingTests) so every
# paid order accrues a realistic per-store COGS once confirmed. Denominated in the offer's currency (EUR).
print(json.dumps({"tenantId": tenant_id, "productId": product_id, "variantId": variant_id or None, "supplierId": supplier_id, "supplyCategory": SUPPLY[supply], "fulfilmentType": FULFIL[fulfilment], "priceMinor": price, "supplierCostMinor": price // 2, "currency": "EUR", "priority": 10, "pricingModel": PRICING[pricing], "billingPeriod": PERIOD[period], "tiers": tiers}))
PY
}

seed_scenario_matrix() {
  local supplier_id="$1" location_id="$2"
  echo "== product/supply/billing scenario matrix =="
  local categories category_id product_search product_id product_body product_json existing_editor variant_id offer_json offer_body offer_id stock_qty availability_status
  categories=$(api "catalog-categories" GET "/api/catalog/categories" "$ADMIN_JAR" "" "expect_2xx")
  category_id=$(printf '%s' "$categories" | json_get '0.id')
  if [[ -z "$category_id" ]]; then
    manifest_append "warnings" "$(json_string "catalog category lookup returned no id; scenario product seeding skipped")"
    return
  fi
  manifest_set "catalog.defaultCategoryId" "$(json_string "$category_id")"

  for code in "${SCENARIO_CODES[@]}"; do
    product_json=$(scenario_product_json "$code" "$category_id")
    product_search=$(api "scenario-$code-lookup" GET "/api/catalog/admin/products?q=e2e-scenario-$code&pageSize=1" "$ADMIN_JAR" "" "allow_4xx")
    product_id=$(printf '%s' "$product_search" | json_get '0.id')
    if [[ -n "$product_id" ]]; then
      existing_editor=$(api "scenario-$code-get-existing" GET "/api/catalog/admin/products/$product_id" "$ADMIN_JAR" "" "allow_4xx")
      product_json=$(python3 -c '
import json, sys
payload, existing = json.loads(sys.argv[1]), json.loads(sys.argv[2])
by_sku = {v.get("sku"): v.get("id") for v in existing.get("variants", [])}
for v in payload.get("variants", []):
    v["id"] = by_sku.get(v.get("sku"))
print(json.dumps(payload))
' "$product_json" "$existing_editor")
      product_body=$(api "scenario-$code-update" PUT "/api/catalog/admin/products/$product_id" "$ADMIN_JAR" "$product_json" "allow_4xx")
    else
      product_body=$(api "scenario-$code-create" POST "/api/catalog/admin/products" "$ADMIN_JAR" "$product_json" "allow_4xx")
      product_id=$(printf '%s' "$product_body" | json_get id)
    fi
    [[ -z "$product_id" ]] && product_id=$(printf '%s' "$product_body" | json_get id)
    if [[ -z "$product_id" ]]; then
      manifest_append "warnings" "$(json_string "scenario product $code did not return an id")"
      continue
    fi
    variant_id=$(printf '%s' "$product_body" | json_get 'variants.0.id')
    if [[ -z "$variant_id" ]]; then
      product_body=$(api "scenario-$code-get" GET "/api/catalog/admin/products/$product_id" "$ADMIN_JAR" "" "allow_4xx")
      variant_id=$(printf '%s' "$product_body" | json_get 'variants.0.id')
    fi
    manifest_set "products.$code.id" "$(json_string "$product_id")"
    manifest_set "products.$code.slug" "$(json_string "e2e-scenario-$code")"
    if [[ -n "$variant_id" ]]; then manifest_set "products.$code.variantId" "$(json_string "$variant_id")"; fi

    # Idempotent on the full offer key: only POST when no offer for this
    # (tenant, product, variant, supplier, storefront=all) already exists, so re-seeds don't pile dups.
    if [[ -z "$(offer_key_exists "$supplier_id" "$product_id" "$variant_id" "")" ]]; then
      offer_json=$(scenario_offer_json "$code" "$product_id" "$variant_id" "$supplier_id")
      offer_body=$(api "scenario-$code-offer" POST "/api/catalog/admin/offers" "$ADMIN_JAR" "$offer_json" "allow_4xx")
      offer_id=$(printf '%s' "$offer_body" | json_get id)
      if [[ -n "$offer_id" ]]; then manifest_set "offers.$code.id" "$(json_string "$offer_id")"; fi
    fi

    case "$code" in
      physical-warehouse-flat|physical-multi-variant-tiered|bundle-mixed-physical) stock_qty=25 ;;
      out-of-stock-hold|inactive-unpublished-private) stock_qty=0 ;;
      *) stock_qty=999 ;;
    esac
    if [[ -n "$location_id" && -n "$variant_id" ]]; then
      api "scenario-$code-stock" POST "/api/fulfillment/admin/inventory/stock" "$ADMIN_JAR" "{\"tenantId\":\"$TENANT_ID\",\"locationId\":\"$location_id\",\"productId\":\"$product_id\",\"variantId\":\"$variant_id\",\"onHand\":$stock_qty}" "allow_4xx" >/dev/null
      manifest_set "fulfillment.stock.$code.onHand" "$stock_qty"
    fi
    availability_status=1  # SupplierStockStatus.Available
    [[ "$code" == "inactive-unpublished-private" ]] && availability_status=2  # SupplierStockStatus.OutOfStock
    api "scenario-$code-availability" POST "/api/fulfillment/admin/dropship/availability" "$ADMIN_JAR" "{\"tenantId\":\"$TENANT_ID\",\"supplierId\":\"$supplier_id\",\"productId\":\"$product_id\",\"variantId\":\"$variant_id\",\"status\":$availability_status,\"externalQuantity\":$stock_qty,\"supplierSku\":\"SUP-$code\"}" "allow_4xx" >/dev/null
  done
}

manifest_get() {
  local dotted_path="$1"
  python3 - "$MANIFEST" "$dotted_path" <<'PY'
import json, sys
path, dotted = sys.argv[1:]
try:
    with open(path, encoding='utf-8') as f:
        cur = json.load(f)
    for part in [p for p in dotted.split('.') if p]:
        cur = cur[int(part)] if isinstance(cur, list) else cur[part]
    if cur is not None:
        print(cur)
except Exception:
    pass
PY
}

# ---- Sellable-checkout helpers -------------------------------------------------------------------------
# Every seed checkout must be one the platform accepts from a real shopper: on a LIVE demo storefront this
# seed manages (never every tenant store — a long-lived DB carries dozens of E2E leftover stores, mostly
# Draft/Paused, which 400 "This storefront is not currently open for orders"), for a product published on
# that store in its currency whose supply passes the approved-supplier gate (ADR-0048), shipped to a country
# the store serves (ADR-0050), from an EMPTY cart. Carts belong to the signed-in user and survive a failed
# checkout, so one failure used to poison every later add ("Cart is in EUR; empty it to shop in AUD") and
# every later checkout of that user (the stale line rode along and failed it again).

DEMO_STORE_KEYS=(demoAu demoEu demoUs demoCa demoUk demoCn demoJp demoKw)
DEMO_PM_PROVIDER_ID="pm_fake_visa_4242_1229"

# demo_storefront_rows: the seed's own demo storefronts that are LIVE (Preview/Active — what checkout
# accepts), one per line as key|id|currency|shipCountry. shipCountry is the first allowlisted country, or a
# home country for the currency when the store ships worldwide (empty allowlist). A demo store that is not
# live is reported loudly and left out, rather than producing checkouts the platform must reject.
demo_storefront_rows() {
  local list ids="" key id
  list=$(api "demo-sf-list" GET "/api/catalog/admin/storefronts?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx")
  for key in "${DEMO_STORE_KEYS[@]}"; do
    id=$(manifest_get "storefronts.$key.id")
    [[ -n "$id" ]] && ids+="$key=$id,"
  done
  printf '%s' "$list" | DEMO_IDS="$ids" python3 -c '
import json, os, sys
HOME = {"AUD": "AU", "EUR": "DE", "USD": "US", "CAD": "CA", "GBP": "GB", "CNY": "CN", "JPY": "JP", "KWD": "KW"}
LIVE = {2, 3}  # StorefrontState.Preview, StorefrontState.Active (enums cross HTTP as numbers)
try:
    stores = json.load(sys.stdin)
except Exception:
    stores = []
wanted = {}
for pair in filter(None, os.environ.get("DEMO_IDS", "").split(",")):
    key, sid = pair.split("=", 1)
    wanted[sid.lower()] = key
listed = {str(s.get("id", "")).lower() for s in stores}
for sid, key in wanted.items():
    if sid not in listed:
        print(f"  !! demo storefront {key} ({sid}) is missing from the storefront list - its orders are skipped", file=sys.stderr)
# Keep the admin list order (by name): seed_storefront_publications gives each store the catalogue page
# of its position, so the order decides WHICH products each demo store publishes — keep it stable.
for s in stores:
    key = wanted.get(str(s.get("id", "")).lower())
    if key is None:
        continue
    state = s.get("state")
    if state not in LIVE:
        print(f"  !! demo storefront {key} is not live (state={state}) - its orders are skipped", file=sys.stderr)
        continue
    cur = s.get("currency") or "EUR"
    ship = (s.get("shipToCountries") or [None])[0] or HOME.get(cur, "AU")
    print("|".join([key, str(s.get("id")), cur, ship]))'
}

# demo_storefront_row <key>: the demo_storefront_rows line for one store (empty when it is not live).
demo_storefront_row() {
  demo_storefront_rows | awk -F'|' -v k="$1" '$1 == k'
}

# shipping_address_json <name> <line1> <country>: a shippingAddress object with a plausible city/postcode.
shipping_address_json() {
  python3 -c '
import json, sys
name, line1, country = sys.argv[1:]
places = {"AU": ("Melbourne", "3000"), "DE": ("Berlin", "10115"), "US": ("New York", "10001"),
          "CA": ("Toronto", "M5H 2N2"), "GB": ("London", "SW1A 1AA"), "CN": ("Shanghai", "200000"),
          "JP": ("Tokyo", "100-0001"), "KW": ("Kuwait City", "13001"), "NZ": ("Auckland", "1010")}
city, postcode = places.get(country, ("Capital", "1000"))
print(json.dumps({"name": name, "line1": line1, "city": city, "postcode": postcode, "country": country}))' "$1" "$2" "$3"
}

# empty_cart <jar> <label>: remove every line from the cart the jar's session owns.
empty_cart() {
  local jar="$1" label="$2" cart line
  cart=$(api "$label-cart-get" GET "/api/ordering/cart/" "$jar" "" "allow_4xx")
  while IFS= read -r line; do
    [[ -n "$line" ]] || continue
    api "$label-cart-clear" DELETE "/api/ordering/cart/items/$line" "$jar" "" "allow_4xx" >/dev/null
  done < <(printf '%s' "$cart" | python3 -c '
import json, sys
try:
    for i in json.load(sys.stdin).get("items", []):
        print(i["productId"] + ("/" + i["variantId"] if i.get("variantId") else ""))
except Exception:
    pass')
}

# pick_sellable_product <storefrontId> <currency> <nth> <label>: echo the nth (0-based, wrapping) product
# SELLABLE on this store. The store-scoped listing gives products published there and priced in its
# currency (it already hides products whose covering offers in that currency are all unapproved). Ordering's
# checkout gate is wider — it refuses a line whose product has ANY active offer (any currency/store) but none
# from an approved supplier — so a product also needs no active offer at all, or one from the approved demo
# supplier. Echoes nothing when the store has no such product.
pick_sellable_product() {
  local sid="$1" cur="$2" nth="$3" label="$4" supplier_id hits pid offers
  local -a sellable=()
  supplier_id=$(manifest_get "entities.demoSupplier.id")
  hits=$(api "$label-prods" GET "/api/catalog/products?storefrontId=$sid&currency=$cur&pageSize=20" "$ADMIN_JAR" "" "allow_4xx")
  while IFS= read -r pid; do
    [[ -n "$pid" ]] || continue
    offers=$(api "$label-offers-${pid:0:8}" GET "/api/catalog/admin/offers?tenantId=$TENANT_ID&product=$pid" "$ADMIN_JAR" "" "allow_4xx")
    if [[ "$(printf '%s' "$offers" | PRODUCT_ID="$pid" SUPPLIER_ID="$supplier_id" python3 -c '
import json, os, sys
pid, sup = os.environ["PRODUCT_ID"].lower(), os.environ["SUPPLIER_ID"].lower()
try:
    rows = json.load(sys.stdin)
    active = [o for o in rows if str(o.get("productId", "")).lower() == pid and str(o.get("status", "")).lower() == "active"]
    print("yes" if not active or any(str(o.get("supplierId", "")).lower() == sup for o in active) else "")
except Exception:
    print("")')" == yes ]]; then
      sellable+=("$pid")
      (( ${#sellable[@]} > nth )) && break
    fi
  done < <(printf '%s' "$hits" | python3 -c '
import json, sys
try:
    print("\n".join(h["id"] for h in json.load(sys.stdin)))
except Exception:
    pass')
  (( ${#sellable[@]} )) || return 0
  echo "${sellable[$(( nth % ${#sellable[@]} ))]}"
}

# checkout_order <name> <jar> <json>: POST a checkout and echo the response body. A 400 is retried a few
# times, 3 s apart: the read models it checks (offer, supplier-approval and storefront copies projected into
# Ordering) can trail a change the seed made seconds earlier, and a 400 books nothing, so a retry cannot
# double-order. ONE summary row records the final outcome as must_2xx — a checkout that still fails is
# unexpected: printed with its body here and failing the seed at the end.
checkout_order() {
  local name="$1" jar="$2" data="$3" body_file code attempt
  body_file="$OUT_DIR/${name//[^A-Za-z0-9_.-]/_}.json"
  for attempt in 1 2 3 4; do
    code=$(curl -sS -k -b "$jar" -c "$jar" -X POST "$GATEWAY/api/ordering/checkout" \
      -H 'content-type: application/json' -d "$data" -o "$body_file" -w '%{http_code}' || true)
    [[ "$code" == 400 && $attempt -lt 4 ]] || break
    printf '  %-40s checkout got a 400 on attempt %s, retrying: %s\n' "$name" "$attempt" "$(head -c 200 "$body_file")" >&2
    sleep 3
  done
  record "$name" POST "/api/ordering/checkout" "$code" "$body_file" "must_2xx"
  printf '  %-40s %s %s -> %s\n' "$name" POST "/api/ordering/checkout" "$code" >&2
  if [[ ! "$code" =~ ^2 ]]; then
    report_unexpected "$name" POST "/api/ordering/checkout" "$code" "$body_file"
  fi
  cat "$body_file"
}

# ensure_demo_payment_method: echo the id of the demo customer's saved (mock) card, saving it once per run.
# A recurring (subscription) line can only be bought by a verified member with a saved payment method or
# direct-debit mandate; LocalMock resolves the card details from the pm_fake_{brand}_{last4}_{expiry} id.
ensure_demo_payment_method() {
  local pm_id pm_json email
  pm_id=$(manifest_get "customers.demo.paymentMethodId")
  if [[ -z "$pm_id" ]]; then
    email=$(manifest_get "customers.demo.email")
    pm_json=$(api "demo-customer-pm" POST "/api/payments/payment-methods/" "$CUSTOMER_JAR" \
      "{\"email\":\"$email\",\"providerPaymentMethodId\":\"$DEMO_PM_PROVIDER_ID\",\"makeDefault\":true}" "must_2xx")
    pm_id=$(printf '%s' "$pm_json" | json_get id)
    if [[ -n "$pm_id" ]]; then manifest_set "customers.demo.paymentMethodId" "$(json_string "$pm_id")"; fi
  fi
  printf '%s' "$pm_id"
}

checkout_scenario() {
  local code="$1" quantity="${2:-1}" customer_email order_body order_id client_secret intent_id status_body status ticket_body ticket_id rma_body rma_id shipments shipment_id package_body package_id subs sub_id
  local product_id variant_id jar
  product_id=$(manifest_get "products.$code.id")
  variant_id=$(manifest_get "products.$code.variantId")
  customer_email=$(manifest_get "customers.demo.email")
  if [[ -z "$product_id" || -z "$variant_id" || -z "$customer_email" ]]; then
    manifest_append "warnings" "$(json_string "checkout_scenario $code skipped: missing product/variant/customer fixture")"
    return
  fi

  # Attribute these EUR demo orders to the EU storefront so revenue posts to its own account (never the
  # shared revenue.sales) — otherwise every generic order shows up as "unassigned (no storefront)". The
  # scenario products are EUR-priced and published to the EU store (seed_scenario_publications).
  local eu_row eu_store ship_country
  eu_row=$(demo_storefront_row demoEu)
  if [[ -z "$eu_row" ]]; then
    echo "  !! checkout_scenario $code skipped: the EU demo storefront is not live" >&2
    manifest_append "warnings" "$(json_string "checkout_scenario $code skipped: EU demo storefront not live")"
    return
  fi
  IFS='|' read -r _ eu_store _ ship_country <<<"$eu_row"

  # A recurring (subscription) line is sold only to a verified member paying with a saved method (the
  # recurring-purchase gate), so those scenarios check out with the demo customer's saved mock card.
  local recurring=0 pm_id="" payment_json=""
  case "$code" in subscription-*) recurring=1 ;; esac
  if (( recurring )); then
    pm_id=$(ensure_demo_payment_method)
    [[ -n "$pm_id" ]] && payment_json=",\"paymentOption\":\"CreditCard\",\"savedPaymentMethodId\":\"$pm_id\""
  fi

  jar="$OUT_DIR/checkout-$code.cookie"
  cp "$CUSTOMER_JAR" "$jar" 2>/dev/null || true
  empty_cart "$jar" "history-$code"
  api "history-$code-cart-add" POST "/api/ordering/cart/items" "$jar" \
    "{\"productId\":\"$product_id\",\"variantId\":\"$variant_id\",\"quantity\":$quantity}" "must_2xx" >/dev/null
  order_body=$(checkout_order "history-$code-checkout" "$jar" \
    "{\"email\":\"$customer_email\",\"storefrontId\":\"$eu_store\",\"shippingAddress\":$(shipping_address_json "Demo Customer" "42 Example Street" "$ship_country"),\"selectedShippingService\":\"Fake Ground\",\"selectedShippingAmountMinor\":499,\"selectedShippingExpiresAt\":\"2999-01-01T00:00:00Z\"$payment_json}")
  order_id=$(printf '%s' "$order_body" | json_get orderId)
  client_secret=$(printf '%s' "$order_body" | json_get clientSecret)
  if [[ -z "$order_id" ]]; then
    manifest_append "warnings" "$(json_string "checkout_scenario $code did not return an order id")"
    return
  fi
  intent_id="${client_secret%_secret_test}"
  [[ -z "$intent_id" || "$intent_id" == "$client_secret" ]] && intent_id="pi_fake_${order_id//-/}"
  # Off-session (saved-method) charges: the mock intent carries the payment-method suffix
  # (FakePaymentProvider: pi_fake_{order}_{providerPaymentMethodId}).
  if [[ -n "$pm_id" ]]; then intent_id="pi_fake_${order_id//-/}_$DEMO_PM_PROVIDER_ID"; fi
  manifest_set "orders.$code.id" "$(json_string "$order_id")"
  manifest_set "payments.$code.intentId" "$(json_string "$intent_id")"

  settle_payment "$intent_id" "" "$jar" "history-$code-pay"
  for _ in $(seq 1 20); do
    sleep 1
    status_body=$(api "history-$code-status" GET "/api/ordering/orders/$order_id/status" "$jar" "" "allow_4xx")
    status=$(printf '%s' "$status_body" | json_get status)
    [[ "$status" == "Confirmed" ]] && break
  done
  if [[ -n "$status" ]]; then manifest_set "orders.$code.status" "$(json_string "$status")"; fi

  ticket_body=$(api "history-$code-ticket" POST "/api/support/tickets" "$jar" \
    "{\"orderId\":\"$order_id\",\"email\":\"$customer_email\",\"reason\":4,\"message\":\"Demo support thread for $code\"}" "allow_4xx")
  ticket_id=$(printf '%s' "$ticket_body" | json_get id)
  if [[ -n "$ticket_id" ]]; then
    manifest_set "support.tickets.$code.id" "$(json_string "$ticket_id")"
    api "history-$code-ticket-message" POST "/api/support/tickets/$ticket_id/messages" "$jar" \
      "{\"body\":\"Additional customer message for $code\"}" "allow_4xx" >/dev/null
  fi

  rma_body=$(api "history-$code-rma" POST "/api/support/rma" "$jar" \
    "{\"orderId\":\"$order_id\",\"reason\":\"Seeded QA RMA for $code\",\"lines\":[{\"productId\":\"$product_id\",\"quantity\":1}]}" "allow_4xx")
  rma_id=$(printf '%s' "$rma_body" | json_get rmaId)
  if [[ -n "$rma_id" ]]; then
    manifest_set "support.rmas.$code.id" "$(json_string "$rma_id")"
    # Seed a spread of RMA lifecycle states so the queue + Mission Control show every stage and button
    # (open / awaiting return / declined / issued). Wait for Requested first so the action never 409s.
    case "$code" in
      physical-warehouse-flat)
        # Approve REQUIRING a physical return → sits in AwaitingReturn (shows "Mark received" + restock).
        rma_wait_state "$rma_id" "Requested" &&
          api "history-$code-rma-approve-return" POST "/api/support/admin/rmas/$rma_id/approve" "$ADMIN_JAR" '{"requireReturn":true}' "allow_4xx" >/dev/null ;;
      physical-dropship-flat)
        # Approve with NO return → refund travels the whole way to RefundIssued.
        rma_wait_state "$rma_id" "Requested" &&
          api "history-$code-rma-approve" POST "/api/support/admin/rmas/$rma_id/approve" "$ADMIN_JAR" '{"requireReturn":false}' "allow_4xx" >/dev/null ;;
      out-of-stock-hold)
        # Deny → Denied.
        rma_wait_state "$rma_id" "Requested" &&
          api "history-$code-rma-deny" POST "/api/support/admin/rmas/$rma_id/deny" "$ADMIN_JAR" "" "allow_4xx" >/dev/null ;;
      *)
        : ;; # leave in Requested (open) — digital / subscription / usage scenarios
    esac
  fi

  shipments=$(api "history-$code-shipments" GET "/api/fulfillment/admin/shipments?orderId=$order_id" "$ADMIN_JAR" "" "allow_4xx")
  shipment_id=$(printf '%s' "$shipments" | json_get '0.id')
  if [[ -n "$shipment_id" ]]; then
    manifest_set "fulfillment.shipments.$code.id" "$(json_string "$shipment_id")"
    package_body=$(api "history-$code-package" POST "/api/fulfillment/admin/shipments/$shipment_id/packages" "$ADMIN_JAR" \
      "{\"tenantId\":\"$TENANT_ID\",\"weightGrams\":500,\"lengthMm\":200,\"widthMm\":150,\"heightMm\":100}" "allow_4xx")
    package_id=$(printf '%s' "$package_body" | json_get id)
    if [[ -n "$package_id" ]]; then
      manifest_set "fulfillment.packages.$code.id" "$(json_string "$package_id")"
      api "history-$code-label" POST "/api/fulfillment/admin/packages/$package_id/label" "$ADMIN_JAR" \
        "{\"tenantId\":\"$TENANT_ID\",\"carrier\":1}" "allow_4xx" >/dev/null
      api "history-$code-tracking-refresh" POST "/api/fulfillment/admin/packages/$package_id/tracking/refresh?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    fi
  fi

  subs=$(api "history-$code-subscriptions" GET "/api/payments/admin/subscriptions?tenantId=$TENANT_ID&email=$customer_email" "$ADMIN_JAR" "" "allow_4xx")
  sub_id=$(printf '%s' "$subs" | json_get '0.id')
  if [[ -n "$sub_id" ]]; then
    manifest_set "subscriptions.$code.id" "$(json_string "$sub_id")"
  fi
}

seed_historical_flows() {
  echo "== historical commerce flows =="
  checkout_scenario "physical-warehouse-flat" 1
  checkout_scenario "physical-dropship-flat" 1
  checkout_scenario "digital-download-onetime" 1
  checkout_scenario "subscription-monthly-flat" 1
  checkout_scenario "subscription-yearly-tiered" 1
  checkout_scenario "usage-api-meter" 3
  checkout_scenario "out-of-stock-hold" 1
}

# Subscription examples: a mix of active and "expired" subscriptions so the monitors show real data.
# An instance is created when an order with a recurring line confirms (the offer's pricingModel is
# Subscription). The domain has no "Expired" status — an ended subscription is Cancelled — so we
# create a few more via extra subscription checkouts and cancel a subset to represent ended terms.
seed_subscription_examples() {
  echo "== subscription examples (active + expired) =="
  local sub_pid sub_vid n jar ck oid intent st sid cancelled=0
  sub_pid=$(manifest_get "products.subscription-monthly-flat.id")
  sub_vid=$(manifest_get "products.subscription-monthly-flat.variantId")
  if [[ -z "$sub_pid" || -z "$sub_vid" ]]; then
    manifest_append "warnings" "$(json_string "subscription examples skipped: monthly subscription fixture missing")"
    return
  fi

  # Subscriptions require the member to purchase with a saved payment method / direct-debit mandate (the
  # subscription purchase gate) — without one, checkout 400s "A saved payment method or direct-debit
  # mandate is required for a subscription". Save a mock card for the demo customer (LocalMock resolves
  # fake card details from the pm_fake_{brand}_{last4}_{expiry} id) and pass its id at checkout.
  local pm_id
  pm_id=$(ensure_demo_payment_method)
  if [[ -z "$pm_id" ]]; then
    manifest_append "warnings" "$(json_string "subscription examples skipped: could not save a payment method")"
    return
  fi

  # A few more active subscriptions, each its own order (subscription is keyed per order).
  local eu_row eu_store ship_country
  eu_row=$(demo_storefront_row demoEu)
  if [[ -z "$eu_row" ]]; then
    manifest_append "warnings" "$(json_string "subscription examples skipped: EU demo storefront not live")"
    return
  fi
  IFS='|' read -r _ eu_store _ ship_country <<<"$eu_row"
  for n in 1 2 3; do
    jar="$OUT_DIR/sub-example-$n.cookie"; cp "$CUSTOMER_JAR" "$jar" 2>/dev/null || true
    empty_cart "$jar" "sub-example-$n"
    api "sub-example-$n-cart" POST "/api/ordering/cart/items" "$jar" \
      "{\"productId\":\"$sub_pid\",\"variantId\":\"$sub_vid\",\"quantity\":1}" "must_2xx" >/dev/null
    ck=$(checkout_order "sub-example-$n-checkout" "$jar" \
      "{\"email\":\"subscriber$n@example.test\",\"storefrontId\":\"$eu_store\",\"paymentOption\":\"CreditCard\",\"savedPaymentMethodId\":\"$pm_id\",\"shippingAddress\":$(shipping_address_json "Subscriber $n" "1 Recur St" "$ship_country")}")
    oid=$(printf '%s' "$ck" | json_get orderId)
    [[ -z "$oid" ]] && continue
    # Off-session (saved-method) charges: the mock intent id carries the payment-method suffix
    # (FakePaymentProvider: pi_fake_{order}_{providerPaymentMethodId}), so settle THAT intent — the bare
    # pi_fake_{order} would 404 and the order would never confirm (no subscription would be created).
    intent="pi_fake_${oid//-/}_$DEMO_PM_PROVIDER_ID"
    settle_payment "$intent" "" "$jar" "sub-example-$n-pay"
    for _ in $(seq 1 15); do sleep 1; st=$(printf '%s' "$(api "sub-example-$n-status" GET "/api/ordering/orders/$oid/status" "$jar" "" "allow_4xx")" | json_get status); [[ "$st" == "Confirmed" ]] && break; done
  done

  # Cancel a subset so the data carries ended ("expired") subscriptions alongside the active ones.
  sleep 3
  local ids
  ids=$(api "sub-examples-list" GET "/api/payments/admin/subscriptions?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx")
  for sid in $(printf '%s' "$ids" | python3 -c "import json,sys; rows=json.load(sys.stdin); print(' '.join(r['id'] for r in rows if r.get('status')=='Active'))" 2>/dev/null); do
    [[ "$cancelled" -ge 2 ]] && break
    api "sub-example-cancel-$sid" POST "/api/payments/admin/subscriptions/$sid/cancel" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    cancelled=$((cancelled + 1))
  done
  manifest_set "subscriptions.examplesCancelled" "$cancelled"
}

login_admin() {
  rm -f "$ADMIN_JAR"
  local code
  code=$(curl -sS -k -c "$ADMIN_JAR" -X POST "$GATEWAY/api/identity/login" \
    -H 'content-type: application/json' \
    -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}" \
    -o "$OUT_DIR/admin-login.json" -w '%{http_code}')
  record "admin-login" "POST" "/api/identity/login" "$code" "$OUT_DIR/admin-login.json" "expect_2xx"
  [[ "$code" == "200" ]] || { echo "Admin login failed ($code). Is the dev stack running?" >&2; exit 1; }
  manifest_set "admin.email" "$(json_string "$ADMIN_EMAIL")"
}

# Look up a user's id by email via the admin users list (echoes the id, or empty). Admin jar required.
user_id_by_email() {
  local email="$1" users
  users=$(api "admin-users-lookup" GET "/api/identity/admin/users?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx")
  printf '%s' "$users" | python3 -c 'import json,sys
want=sys.argv[1].lower()
try:
    for u in json.load(sys.stdin):
        if str(u.get("email","")).lower()==want:
            print(u["id"]); break
except Exception:
    pass' "$email"
}

# Ready-to-use test logins (dev only): verify the seeded demo customer (so verified-only features like
# reviews work), and provision a least-privilege supplier-portal login bound to the demo supplier entity.
provision_test_logins() {
  local entity_id="$1"
  local cust_email="demo.customer.$RUN_ID@example.test"
  local sup_email="supplier@3commerce.local" sup_pass="Supplier-password-123"

  # Verify the demo customer the proper way (publishes EmailVerified → attaches any prior guest orders).
  local cust_id
  cust_id=$(user_id_by_email "$cust_email")
  if [[ -n "$cust_id" ]]; then
    api "customer-verify-email" POST "/api/identity/admin/users/$cust_id/verify-email?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    manifest_set "customers.demo.emailVerified" "true"
    # Sign in again so the customer's session carries email_verified=true: the gateway caches session
    # introspection (up to 60 s) per token, and the recurring-purchase gate refuses an unverified member.
    api_noauth "login-customer-verified" POST "/api/identity/login" "$CUSTOMER_JAR" \
      "{\"email\":\"$cust_email\",\"password\":\"$(manifest_get customers.demo.password)\"}" "must_2xx" >/dev/null
  fi

  # Supplier-portal login scoped to the demo supplier (role=supplier, no admin rights).
  if [[ -n "$entity_id" ]]; then
    api_noauth "register-supplier-login" POST "/api/identity/register" "$SUPPLIER_JAR" \
      "{\"email\":\"$sup_email\",\"password\":\"$sup_pass\"}" "allow_4xx" >/dev/null
    local sup_id
    sup_id=$(user_id_by_email "$sup_email")
    if [[ -n "$sup_id" ]]; then
      api "make-supplier" POST "/api/identity/admin/users/$sup_id/make-supplier?tenantId=$TENANT_ID" "$ADMIN_JAR" \
        "{\"supplierEntityId\":\"$entity_id\"}" "allow_4xx" >/dev/null
      manifest_set "suppliers.demo.email" "$(json_string "$sup_email")"
      manifest_set "suppliers.demo.password" "$(json_string "$sup_pass")"
      manifest_set "suppliers.demo.entityId" "$(json_string "$entity_id")"
    fi
  fi
}

seed_smoke() {
  echo "== smoke demo data =="
  login_admin
  api "catalog-import" POST "/api/catalog/admin/import-runs" "$ADMIN_JAR" "" "expect_2xx" >/dev/null

  local email="demo.customer.$RUN_ID@example.test"
  local password="Demo-password-123"
  api_noauth "register-customer" POST "/api/identity/register" "$CUSTOMER_JAR" \
    "{\"email\":\"$email\",\"password\":\"$password\"}" "allow_4xx" >/dev/null
  api_noauth "login-customer" POST "/api/identity/login" "$CUSTOMER_JAR" \
    "{\"email\":\"$email\",\"password\":\"$password\"}" "expect_2xx" >/dev/null
  api "customer-profile" PUT "/api/identity/me" "$CUSTOMER_JAR" \
    '{"givenName":"Demo","familyName":"Customer"}' "allow_4xx" >/dev/null
  api "customer-address-shipping" POST "/api/identity/me/addresses" "$CUSTOMER_JAR" \
    '{"purpose":3,"isDefault":true,"name":"Demo Home","line1":"42 Example Street","line2":"Unit 3","city":"Melbourne","postcode":"3000","country":"AU"}' "allow_4xx" >/dev/null

  manifest_set "customers.demo.email" "$(json_string "$email")"
  manifest_set "customers.demo.password" "$(json_string "$password")"
  for code in "${SCENARIO_CODES[@]}"; do
    manifest_append "scenarioCodes" "$(json_string "$code")"
    manifest_set "products.$code.code" "$(json_string "$code")"
    manifest_set "products.$code.name" "$(json_string "E2E Scenario ${code}")"
  done
}

# Publish a distinct handful of image-having, currency-matching products to each storefront, so every
# storefront shows ONLY its own published catalog (its merchandising scope) instead of the whole catalog.
# Distinct page per storefront keeps the published sets non-overlapping, which makes isolation obvious.
# Only the seed's own (live) demo stores: a long-lived DB also holds E2E leftover stores, and walking those
# shifted every demo store onto a later, often empty, catalog page.
seed_storefront_publications() {
  echo "== storefront product publications =="
  local page=0 sid cur pid hits
  while IFS='|' read -r _ sid cur _; do
    [[ -n "$sid" && -n "$cur" ]] || continue
    page=$((page + 1))
    hits=$(api "sf-pub-$cur-list" GET "/api/catalog/products?currency=$cur&pageSize=5&page=$page" "$ADMIN_JAR" "" "allow_4xx")
    while IFS= read -r pid; do
      [[ -n "$pid" ]] || continue
      # fulfillmentSource=2 (Warehouse) + an image + a visible priced variant satisfy publish readiness.
      api "sf-pub-$cur-assign" POST "/api/catalog/admin/storefronts/$sid/products" "$ADMIN_JAR" \
        "{\"productId\":\"$pid\",\"fulfillmentSource\":2}" "allow_4xx" >/dev/null
      api "sf-pub-$cur-publish" POST "/api/catalog/admin/storefronts/$sid/products/$pid/publish" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    # Imported catalogue only: never the seed's own scenario fixtures (seed_scenario_publications places the
    # physical ones on the EU store deliberately) nor products E2E specs create ("e2e" in slug/title). On a
    # long-lived DB those leftovers push the scenario products into later pages, and publishing them made
    # e.g. "E2E Scenario out-of-stock-hold" the EU store's first product (unbuyable) — failing the PDP/cart
    # specs. On a fresh DB these pages hold no fixtures, so nothing changes there.
    done < <(printf '%s' "$hits" | python3 -c "import sys,json
try:
  for h in json.load(sys.stdin):
    tag = (h.get('slug','') + ' ' + h.get('title','')).lower()
    if h.get('imageUrl') and 'e2e' not in tag: print(h['id'])
except Exception: pass")
  done < <(demo_storefront_rows)
}

# Publish the EUR-priced demo SCENARIO products (created by seed_scenario_matrix) to the EU demo store, so
# they surface on that store's storefront home/PDP. The browser E2E specs pin the EU store and click through
# a[href*="physical-warehouse"] on the store home, and the storefront PDP 404s for a product NOT published
# to the pinned store — without this the scenario products exist only in the global catalog, never on a
# store, so cart-checkout and collect-at-warehouse can't reach them. We publish only the PHYSICAL scenarios
# (the ones a storefront checkout flow exercises); the non-physical scenarios are covered by the direct-PDP
# catalog-product-types spec, which is store-agnostic. The home lists newest-first (CreatedAt DESC), and the
# physical scenarios are the newest EUR products, so physical-warehouse-flat lands within the featured page.
seed_scenario_publications() {
  echo "== scenario product storefront publications (EU demo store) =="
  local eu_store code pid
  eu_store=$(manifest_get "storefronts.demoEu.id")
  [[ -n "$eu_store" ]] || { manifest_append "warnings" "$(json_string "seed_scenario_publications: no EU demo store")"; return; }
  for code in physical-warehouse-flat physical-dropship-flat physical-multi-variant-tiered; do
    pid=$(manifest_get "products.$code.id")
    [[ -n "$pid" ]] || { manifest_append "warnings" "$(json_string "seed_scenario_publications: no product id for $code")"; continue; }
    # fulfillmentSource=2 (Warehouse) + the product's image + a EUR-priced variant satisfy publish readiness.
    api "scenario-pub-$code-assign" POST "/api/catalog/admin/storefronts/$eu_store/products" "$ADMIN_JAR" \
      "{\"productId\":\"$pid\",\"fulfillmentSource\":2}" "allow_4xx" >/dev/null
    api "scenario-pub-$code-publish" POST "/api/catalog/admin/storefronts/$eu_store/products/$pid/publish" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    manifest_set "products.$code.publishedTo" "$(json_string "demoEu")"
  done
}

# Settle a payment, retrying until the checkout saga has created the payment intent — a bare
# simulate-payment fired right after checkout races intent creation and 500s (leaving the order unpaid,
# so it never posts to the ledger). Args: <intentId> <amountQuery e.g. "?amountMinor=1234" or ""> <jar>
# <label>. Polls up to ~10s; records the final outcome for the run summary.
settle_payment() {
  local intent="$1" amountQuery="$2" jar="$3" label="$4" code i bf
  bf="$OUT_DIR/${label//[^A-Za-z0-9_.-]/_}.json"
  code=000
  for i in $(seq 1 10); do
    code=$(curl -sS -k -b "$jar" -c "$jar" -X POST "$GATEWAY/api/payments/dev/simulate-payment/$intent$amountQuery" -o "$bf" -w '%{http_code}' 2>/dev/null || echo 000)
    [[ "$code" =~ ^2 ]] && break
    sleep 1
  done
  record "$label" POST "/api/payments/dev/simulate-payment" "$code" "$bf" "allow_4xx"
  printf '  %-40s %s simulate-payment -> %s\n' "$label" POST "$code" >&2
}

# Convenience for callers that hold an order id + gross (fake intent is pi_fake_{orderId-no-dashes}).
settle_order_payment() {
  settle_payment "pi_fake_${1//-/}" "?amountMinor=$2" "$3" "$4"
}

# Give every storefront's products a COSTED offer in the store's OWN currency, so orders placed on any
# store accrue COGS (real per-store margin in Financials — not just the EU scenario store). Run BEFORE
# the order steps so the OfferChanged→OfferCopy projection has time to land before checkout resolves it.
seed_store_product_costs() {
  echo "== per-storefront costed offers (so every store accrues COGS) =="
  local supplier_id sid cur
  supplier_id=$(manifest_get "entities.demoSupplier.id")
  [[ -n "$supplier_id" ]] || { manifest_append "warnings" "$(json_string "seed_store_product_costs: no demo supplier")"; return; }
  while IFS='|' read -r _ sid cur _; do
    [[ -n "$sid" && -n "$cur" ]] || continue
    # Each product this store sells, with a best-effort unit price → a supplier cost of ~half.
    api "cost-prods-$cur" GET "/api/catalog/products?storefrontId=$sid&currency=$cur&pageSize=20" "$ADMIN_JAR" "" "allow_4xx" | python3 -c "import sys,json
try:
  d=json.load(sys.stdin)
  for p in (d if isinstance(d,list) else d.get('items',[])):
    price=p.get('minPriceMinor') or p.get('priceMinor') or 2000
    print(p['id']+'|'+str(price))
except Exception: pass" | while IFS='|' read -r pid price; do
      [[ -n "$pid" ]] || continue
      # These COGS offers are all-storefront (StorefrontId=null), so the same product reached through
      # several stores of the same currency collapses to ONE key — guard so the loop creates it once,
      # and re-seeds stay a no-op. (First currency to reach a product wins its costed offer.)
      [[ -z "$(offer_key_exists "$supplier_id" "$pid" "" "")" ]] || continue
      api "cost-offer-$cur-${pid:0:8}" POST "/api/catalog/admin/offers" "$ADMIN_JAR" \
        "{\"tenantId\":\"$TENANT_ID\",\"productId\":\"$pid\",\"variantId\":null,\"supplierId\":\"$supplier_id\",\"supplyCategory\":1,\"fulfilmentType\":1,\"priceMinor\":$price,\"supplierCostMinor\":$((price/2)),\"currency\":\"$cur\",\"priority\":5}" "allow_4xx" >/dev/null
    done
  done < <(demo_storefront_rows)
}

# Place a couple of paid orders on EACH demo storefront in ITS OWN currency, rotating the PSP, so the
# per-storefront dashboard/Financials show real revenue for every store — not just the tenant default.
# (checkout_scenario's orders are attributed to the EU demo store.)
seed_storefront_orders() {
  echo "== per-storefront attributed orders =="
  local key sid cur ship pid jar body oid gross opt idx=0
  while IFS='|' read -r key sid cur ship; do
    [[ -n "$sid" && -n "$cur" ]] || continue
    for n in 1 2; do
      idx=$((idx + 1))
      case $((idx % 3)) in 0) opt=CreditCard ;; 1) opt=PayPal ;; 2) opt=Polar ;; esac
      # Two DIFFERENT sellable products of this store (the store's own catalogue, in its currency).
      pid=$(pick_sellable_product "$sid" "$cur" "$((n - 1))" "sf-ord-$cur-prod-$n")
      if [[ -z "$pid" ]]; then
        echo "  !! sf-ord $key: no sellable product published on the store in $cur — order $n skipped" >&2
        manifest_append "warnings" "$(json_string "seed_storefront_orders: $key has no sellable $cur product")"
        continue
      fi
      # Fresh session with an EMPTY cart, so each order's single-currency cart is clean; admin satisfies
      # the customer policy for cart/checkout. StorefrontId in the body attributes the order to the store.
      jar="$OUT_DIR/sf-ord-$cur-$n.cookie"; : >"$jar"
      api_noauth "sf-ord-login-$cur-$n" POST "/api/identity/login" "$jar" \
        '{"email":"admin@3commerce.local","password":"dev-admin-password-1"}' "must_2xx" >/dev/null
      empty_cart "$jar" "sf-ord-$cur-$n"
      api "sf-ord-cart-$cur-$n" POST "/api/ordering/cart/items" "$jar" \
        "{\"productId\":\"$pid\",\"quantity\":1,\"currency\":\"$cur\"}" "must_2xx" >/dev/null
      body=$(checkout_order "sf-ord-checkout-$cur-$n" "$jar" \
        "{\"email\":\"store-$cur@example.test\",\"storefrontId\":\"$sid\",\"paymentOption\":\"$opt\",\"shippingAddress\":$(shipping_address_json "Store Demo" "1 Demo St" "$ship")}")
      oid=$(printf '%s' "$body" | json_get orderId); gross=$(printf '%s' "$body" | json_get grossMinor)
      [[ -n "$oid" ]] || continue
      settle_order_payment "$oid" "$gross" "$jar" "sf-ord-pay-$cur-$n"
    done
  done < <(demo_storefront_rows)
}

# Register THREE more real (verified) shopper accounts per storefront, each buying on its home store in
# that store's currency — and some also buying on a DIFFERENT store (cross-storefront), so per-store
# Financials show orders spread across many customers, including shoppers who span stores. Verified so
# they also work for verified-only features (reviews). Best-effort: skips a store with no priced product.
seed_extra_customers() {
  echo "== extra per-storefront customers + cross-storefront orders =="
  # The live demo stores as key|id|currency|shipCountry, in an array so we can pick a DIFFERENT store for
  # cross-store orders.
  local -a rows=()
  while IFS= read -r r; do [[ -n "$r" ]] && rows+=("$r"); done < <(demo_storefront_rows)
  local n=${#rows[@]}
  [[ "$n" -ge 1 ]] || { manifest_append "warnings" "$(json_string "seed_extra_customers: no live demo storefronts")"; return; }

  # Buy one unit of a SELLABLE product of store $sid (its own catalogue, in its currency $cur, shipped to
  # $ship), as the customer holding cookie $jar, from an empty cart. Shoppers spread over the store's
  # products ($nth). Echoes nothing.
  place_order_on() {
    local sid="$1" cur="$2" ship="$3" jar="$4" email="$5" opt="$6" nth="$7" pid body oid gross
    pid=$(pick_sellable_product "$sid" "$cur" "$nth" "xc-prod-$cur-$nth")
    if [[ -z "$pid" ]]; then
      echo "  !! xc $email: no sellable product published on store $sid in $cur — order skipped" >&2
      manifest_append "warnings" "$(json_string "seed_extra_customers: store $sid has no sellable $cur product")"
      return 0
    fi
    empty_cart "$jar" "xc-$cur-$email"
    api "xc-cart-$cur-$email" POST "/api/ordering/cart/items" "$jar" \
      "{\"productId\":\"$pid\",\"quantity\":1,\"currency\":\"$cur\"}" "must_2xx" >/dev/null
    body=$(checkout_order "xc-checkout-$cur-$email" "$jar" \
      "{\"email\":\"$email\",\"storefrontId\":\"$sid\",\"paymentOption\":\"$opt\",\"shippingAddress\":$(shipping_address_json "Shopper" "7 Buyer Rd" "$ship")}")
    oid=$(printf '%s' "$body" | json_get orderId); gross=$(printf '%s' "$body" | json_get grossMinor)
    [[ -n "$oid" ]] || return 0
    settle_order_payment "$oid" "$gross" "$jar" "xc-pay-$cur-$email"
    # The verified buyer rates + comments on what they bought — reviews are verified-only (#131/#154), so
    # this is the natural place to generate real review data for product pages, the storefront and admin.
    local rating=$(( (${#email} % 3) + 3 )) # 3..5 stars, deterministic per shopper
    api "xc-review-$cur-$email" POST "/api/catalog/products/$pid/reviews" "$jar" \
      "{\"rating\":$rating,\"comment\":\"Great $cur buy — exactly as described and shipped fast.\"}" "allow_4xx" >/dev/null
  }

  local i idx=0
  for ((i = 0; i < n; i++)); do
    local sid cur ship
    IFS='|' read -r _ sid cur ship <<<"${rows[$i]}"
    # The next store in the list (wraps) is where this store's first shopper also buys — cross-storefront.
    local xi=$(((i + 1) % n)) xsid xcur xship
    IFS='|' read -r _ xsid xcur xship <<<"${rows[$xi]}"
    local c
    for c in 1 2 3; do
      idx=$((idx + 1))
      local opt; case $((idx % 3)) in 0) opt=CreditCard ;; 1) opt=PayPal ;; 2) opt=Polar ;; esac
      local email="shopper.$RUN_ID.$cur.$c@example.test" password="Shopper-password-123"
      local jar="$OUT_DIR/xc-$cur-$c.cookie"; : >"$jar"
      api_noauth "xc-register-$cur-$c" POST "/api/identity/register" "$jar" \
        "{\"email\":\"$email\",\"password\":\"$password\"}" "allow_4xx" >/dev/null
      # Verify (proper path: publishes EmailVerified) so the shopper works for verified-only features too.
      local cid; cid=$(user_id_by_email "$email")
      [[ -n "$cid" ]] && api "xc-verify-$cur-$c" POST "/api/identity/admin/users/$cid/verify-email?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
      api_noauth "xc-login-$cur-$c" POST "/api/identity/login" "$jar" \
        "{\"email\":\"$email\",\"password\":\"$password\"}" "must_2xx" >/dev/null
      api "xc-addr-$cur-$c" POST "/api/identity/me/addresses" "$jar" \
        '{"purpose":3,"isDefault":true,"name":"Shopper Home","line1":"7 Buyer Rd","city":"City","postcode":"2000","country":"AU"}' "allow_4xx" >/dev/null
      # Home-store order in this store's currency.
      place_order_on "$sid" "$cur" "$ship" "$jar" "$email" "$opt" "$((c - 1))"
      # Shopper #1 of each store ALSO buys on the next store (cross-storefront), when there's more than one.
      if [[ "$c" == "1" && "$n" -gt 1 ]]; then
        place_order_on "$xsid" "$xcur" "$xship" "$jar" "$email" "$opt" 2
      fi
    done
  done
}

seed_full() {
  seed_smoke
  echo "== full best-effort operator data =="

  local entity_json entity_id supplier_id location_json location_id carrier_json carrier_id product_json product_id variant_id variant_json price_product_id
  # Idempotency: the create endpoint always inserts a NEW entity, so re-running the seed against an
  # existing DB would spawn duplicate "Demo Supplier" rows (breaking the admin Suppliers list/e2e).
  # Reuse the existing demo supplier by legalName when one is already present.
  entity_json=$(api "entity-list-suppliers" GET "/api/entity/entities?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx")
  entity_id=$(printf '%s' "$entity_json" | python3 -c 'import json,sys
try:
    d = json.load(sys.stdin)
    m = [e for e in d if isinstance(e, dict) and e.get("legalName") == "Demo Supplier Pty Ltd"]
    print(m[0]["id"] if m else "")
except Exception:
    print("")')
  # Enums bind as numbers (System.Text.Json default): EntityType.Company=2, EntityRoleKind.Supplier=2.
  if [[ -z "$entity_id" ]]; then
    entity_json=$(api "entity-create-supplier" POST "/api/entity/entities" "$ADMIN_JAR" \
      "{\"tenantId\":\"$TENANT_ID\",\"type\":2,\"legalName\":\"Demo Supplier Pty Ltd\",\"tradingName\":\"Demo Supplier\",\"roles\":[2]}" "allow_4xx")
    entity_id=$(printf '%s' "$entity_json" | json_get id)
  fi
  if [[ -n "$entity_id" ]]; then
    api "entity-supplier-start" POST "/api/entity/entities/$entity_id/suppliers" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    api "entity-duplicate-scan" POST "/api/entity/entities/$entity_id/duplicate-warnings/scan" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    # DECISION A (approval-gated availability): a supplier's offers only count once the supplier is APPROVED
    # (SupplierOnboardingState.Active). Drive the full onboarding so dev-up produces a WORKING storefront —
    # add the readiness data (verified ABN, ops email, warehouse address), then submit → verify → activate.
    # Activating publishes SupplierApprovalChanged(Approved) → Catalog + Ordering project it, so the seeded
    # offers below all resolve. Skipping this would leave every offer from an unapproved supplier and hide
    # the whole demo catalog / block checkout. Idempotent: re-runs 4xx harmlessly on the already-active path.
    local ident_body ident_id
    ident_body=$(api "entity-supplier-abn" POST "/api/entity/entities/$entity_id/identifiers" "$ADMIN_JAR" \
      '{"type":1,"value":"12345678901"}' "allow_4xx")
    ident_id=$(printf '%s' "$ident_body" | json_get 'identifiers.0.id')
    [[ -n "$ident_id" ]] && api "entity-supplier-abn-verify" POST "/api/entity/entities/$entity_id/identifiers/$ident_id/verify" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    # Idempotency: re-running the seed reuses the existing demo supplier (above), so an unconditional
    # POST here appends a SECOND identical ops contact (purpose=3/kind=1/same value) on every re-run —
    # the "Operations · Email · ops@demo-supplier.test" duplicate on the admin Suppliers page. Only add
    # it when it's not already present. (The Entity add-contact path now also rejects exact duplicates,
    # but we still gate here so the seed stays a clean no-op rather than a logged 4xx on re-run.)
    local existing_contacts existing_ops_contact
    existing_contacts=$(api "entity-supplier-detail" GET "/api/entity/entities/$entity_id" "$ADMIN_JAR" "" "allow_4xx")
    existing_ops_contact=$(printf '%s' "$existing_contacts" | python3 -c 'import json,sys
try:
    d = json.load(sys.stdin)
    cs = d.get("contacts", []) if isinstance(d, dict) else []
    hit = any(isinstance(c, dict) and c.get("purpose") == 3 and c.get("kind") == 1
              and (c.get("value") or "").strip().lower() == "ops@demo-supplier.test" for c in cs)
    print("yes" if hit else "")
except Exception:
    print("")')
    if [[ -z "$existing_ops_contact" ]]; then
      api "entity-supplier-contact" POST "/api/entity/entities/$entity_id/contacts" "$ADMIN_JAR" \
        '{"purpose":3,"kind":1,"value":"ops@demo-supplier.test"}' "allow_4xx" >/dev/null
    fi
    api "entity-supplier-address" POST "/api/entity/entities/$entity_id/addresses" "$ADMIN_JAR" \
      '{"purpose":4,"line1":"1 Supplier Way","line2":null,"city":"Sydney","region":"NSW","postcode":"2000","countryCode":"AU"}' "allow_4xx" >/dev/null
    api "entity-supplier-submit" POST "/api/entity/entities/$entity_id/suppliers/submit-verification" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    api "entity-supplier-verify-complete" POST "/api/entity/entities/$entity_id/suppliers/verification-complete" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    api "entity-supplier-activate" POST "/api/entity/entities/$entity_id/suppliers/activate" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    # Idempotency: supplier change-requests are legitimately repeatable (an operator/supplier may raise
    # the same change twice on purpose), so we DON'T dedupe at the endpoint — we just avoid the seed
    # itself re-adding its own demo request on every re-run (which showed TWO identical "Demo supplier
    # contact update" entries in the console's change-requests section). Skip when a matching seeded
    # request for this entity already exists. Match on entity + summary + the seed marker in detail.
    local existing_change_requests existing_seeded_cr
    existing_change_requests=$(api "entity-change-request-list" GET "/api/entity/entities/suppliers/change-requests?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx")
    existing_seeded_cr=$(printf '%s' "$existing_change_requests" | ENTITY_ID="$entity_id" python3 -c 'import json,os,sys
try:
    d = json.load(sys.stdin)
    rows = d if isinstance(d, list) else []
    eid = (os.environ.get("ENTITY_ID") or "").lower()
    hit = any(isinstance(r, dict) and str(r.get("entityId", "")).lower() == eid
              and (r.get("summary") or "") == "Demo supplier contact update"
              and (r.get("detail") or "") == "Seeded by scripts/dev-dummy-data.sh" for r in rows)
    print("yes" if hit else "")
except Exception:
    print("")')
    if [[ -z "$existing_seeded_cr" ]]; then
      api "entity-change-request" POST "/api/entity/entities/$entity_id/suppliers/change-requests?tenantId=$TENANT_ID" "$ADMIN_JAR" \
        '{"type":2,"summary":"Demo supplier contact update","detail":"Seeded by scripts/dev-dummy-data.sh"}' "allow_4xx" >/dev/null
    fi
    supplier_id="$entity_id"
    manifest_set "entities.demoSupplier.id" "$(json_string "$entity_id")"
  else
    supplier_id="00000000-0000-0000-0000-000000000001"
    manifest_append "warnings" "$(json_string "entity-create-supplier did not return an id; using fallback supplier id")"
  fi

  # Ready-to-use test logins: verified demo customer + a supplier-portal login bound to the demo supplier.
  provision_test_logins "$entity_id"

  # Idempotent: (TenantId, Cid) is unique — only create the demo campaign when absent.
  local campaigns
  campaigns=$(api "marketing-campaign-list" GET "/api/marketing/admin/campaigns?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx")
  if ! printf '%s' "$campaigns" | grep -qi '"cid":"demo10"'; then
    api "marketing-campaign" POST "/api/marketing/admin/campaigns" "$ADMIN_JAR" \
      '{"tenantId":"00000000-0000-0000-0000-000000000001","cid":"DEMO10","name":"Demo launch campaign","startsAt":null,"endsAt":null}' "allow_4xx" >/dev/null
  fi
  # CreatePriceRequest requires a ProductId; attach the demo price to the first imported catalog
  # product. Enums bind as numbers (PricingModel.Tiered=4, BillingPeriod.Monthly=2); tier fields are
  # FromQuantity/UnitPriceMinor.
  local price_products
  price_products=$(api "pricing-product-lookup" GET "/api/catalog/admin/products?tenantId=$TENANT_ID&pageSize=1" "$ADMIN_JAR" "" "allow_4xx")
  # Read the id inline: json_get returns empty here (its heredoc consumes python stdin, so
  # json.load reads nothing) — tracked separately; this step must not depend on it.
  price_product_id=$(printf '%s' "$price_products" | python3 -c 'import json,sys
try:
    d = json.load(sys.stdin); print(d[0]["id"] if isinstance(d, list) and d else "")
except Exception:
    print("")')
  if [[ -n "$price_product_id" ]]; then
    api "pricing-price" POST "/api/pricing/admin/prices" "$ADMIN_JAR" \
      "{\"tenantId\":\"$TENANT_ID\",\"productId\":\"$price_product_id\",\"amountMinor\":1999,\"currency\":\"EUR\",\"pricingModel\":4,\"billingPeriod\":2,\"tiers\":[{\"fromQuantity\":1,\"unitPriceMinor\":1999},{\"fromQuantity\":10,\"unitPriceMinor\":1499}]}" "allow_4xx" >/dev/null
  else
    manifest_append "warnings" "$(json_string "pricing-price skipped: no catalog product id available")"
  fi

  upsert_demo_storefront "demoAu" "Demo AU Store" "http://localhost:3000/au" "AUD" 1 1000
  upsert_demo_storefront "demoEu" "Demo EU Store" "http://localhost:3000/eu" "EUR" 2 2000
  upsert_demo_storefront "demoUs" "Demo US Store" "http://localhost:3000/us" "USD" 3 825
  # More currencies so the dashboards/Ledger/Financials/Mission Control show a real multi-currency book:
  # CA (CAD, 5% GST exclusive like US), UK (GBP, 20% VAT inclusive like EU). No FX — amounts relabel into
  # the store currency (ADR-0041 no-FX posture), so these need no separate per-currency product prices.
  upsert_demo_storefront "demoCa" "Demo CA Store" "http://localhost:3000/ca" "CAD" 3 500
  upsert_demo_storefront "demoUk" "Demo UK Store" "http://localhost:3000/uk" "GBP" 2 2000
  upsert_demo_storefront "demoCn" "Demo CN Store" "http://localhost:3000/cn" "CNY" 2 1300
  # JP (JPY) is the 0-decimal currency (currency_4): proves a 0-decimal code flows registry -> storefront
  # -> sale -> dashboards and renders with no decimals (¥3,300, not ¥33.00). taxRegime 2 (inclusive) like
  # CN/UK/EU; 10% rate (1000 bps).
  upsert_demo_storefront "demoJp" "Demo JP Store" "http://localhost:3000/jp" "JPY" 2 1000
  upsert_demo_storefront "demoKw" "Demo KW Store" "http://localhost:3000/kw" "KWD" 2 500

  # Payment accounts are per-storefront (ADR-0042/0043): give each demo storefront its own default
  # account, and submit + activate it so the storefront is payment-ready (the go-live gate needs an
  # ACTIVE account — a Test-mode stripe account activates without an external ref).
  local pay_key pay_sf_id pay_acct_json pay_acct_id
  for pay_key in demoEu demoAu demoUs demoCa demoUk demoCn demoJp demoKw; do
    pay_sf_id=$(manifest_get "storefronts.$pay_key.id")
    [[ -n "$pay_sf_id" ]] || continue
    pay_acct_json=$(api "payment-account-$pay_key" POST "/api/payments/admin/payment-accounts" "$ADMIN_JAR" \
      "{\"tenantId\":\"$TENANT_ID\",\"storefrontId\":\"$pay_sf_id\",\"name\":\"Demo Stripe test account\",\"provider\":\"stripe\",\"mode\":1,\"isDefaultForStorefront\":true,\"externalAccountRef\":null}" "allow_4xx")
    pay_acct_id=$(printf '%s' "$pay_acct_json" | json_get id)
    if [[ -n "$pay_acct_id" ]]; then
      api "payment-account-$pay_key-submit" POST "/api/payments/admin/payment-accounts/$pay_acct_id/submit" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
      api "payment-account-$pay_key-activate" POST "/api/payments/admin/payment-accounts/$pay_acct_id/activate" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    fi
  done
  api "supplier-bank" POST "/api/payments/admin/supplier-payouts/bank-accounts" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"supplierEntityId\":\"$supplier_id\",\"accountName\":\"Demo Supplier Pty Ltd\",\"bankCountry\":\"AU\",\"routingNumberMasked\":\"***123\",\"accountNumberMasked\":\"****1234\",\"accountTokenRef\":\"vault_demo_supplier_bank\"}" "allow_4xx" >/dev/null
  api "xero-mapping" POST "/api/payments/admin/xero/mappings" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"ledgerAccountCode\":\"revenue.sales\",\"xeroAccountCode\":\"200\",\"scope\":\"TenantDefault\",\"storefrontId\":null,\"categoryId\":null,\"supplierId\":null,\"productId\":null}" "allow_4xx" >/dev/null

  location_json=$(api "fulfillment-location" POST "/api/fulfillment/admin/inventory/locations" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"name\":\"Demo warehouse\",\"kind\":1,\"entityId\":\"$supplier_id\",\"addressId\":null}" "allow_4xx")
  location_id=$(printf '%s' "$location_json" | json_get id)
  if [[ -n "$location_id" ]]; then manifest_set "fulfillment.locations.demoWarehouse.id" "$(json_string "$location_id")"; fi

  # Carriers are per-storefront (ADR-0042): give each demo storefront its own active default carrier.
  local sf_key sf_id
  for sf_key in demoEu demoAu demoUs; do
    sf_id=$(manifest_get "storefronts.$sf_key.id")
    [[ -n "$sf_id" ]] || continue
    carrier_json=$(api "fulfillment-carrier-$sf_key" POST "/api/fulfillment/admin/carriers" "$ADMIN_JAR" \
      "{\"tenantId\":\"$TENANT_ID\",\"storefrontId\":\"$sf_id\",\"carrier\":1,\"credentialRef\":null}" "allow_4xx")
    carrier_id=$(printf '%s' "$carrier_json" | json_get id)
    if [[ -n "$carrier_id" ]]; then
      api "fulfillment-carrier-$sf_key-activate" POST "/api/fulfillment/admin/carriers/$carrier_id/activate?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
      api "fulfillment-carrier-$sf_key-default" POST "/api/fulfillment/admin/carriers/$carrier_id/default?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
      manifest_set "fulfillment.carriers.$sf_key.id" "$(json_string "$carrier_id")"
    fi
  done

  api "usage-provision" POST "/api/usage/admin/usage/provision" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"customerEmail\":\"usage.demo@example.test\",\"meter\":3,\"includedQuantity\":1000,\"overageAllowed\":true,\"overageUnitPriceMinor\":5,\"currency\":\"EUR\",\"periodEnd\":null}" "allow_4xx" >/dev/null
  api "usage-record" POST "/api/usage/admin/usage/record" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"customerEmail\":\"usage.demo@example.test\",\"meter\":3,\"quantity\":42,\"referenceId\":\"dev-dummy-$RUN_ID\"}" "allow_4xx" >/dev/null
  manifest_set "usage.demo.email" '"usage.demo@example.test"'
  manifest_set "usage.demo.meterCode" '"api-calls"'

  seed_scenario_matrix "$supplier_id" "${location_id:-}"
  # Give outbox/bus projections (Catalog -> Ordering/Fulfillment/Support) a short window before
  # driving customer flows that depend on ProductCopy/OfferCopy/OrderSnapshot read models.
  sleep 5
  seed_historical_flows
  seed_storefront_publications
  seed_scenario_publications
  seed_store_product_costs
  seed_storefront_orders
  seed_extra_customers
  seed_subscription_examples

  # The imported-catalog sample: a sellable product published on the EU demo store. It already carries the
  # demo supplier's costed EUR offer (seed_store_product_costs). This used to take the admin list's first
  # product BY TITLE — on a long-lived DB an E2E leftover such as "Approval Gate E2E" — and attach a
  # 24.99 EUR demo offer to it (re-pricing it and making an unapproved-supplier fixture sellable).
  local eu_row eu_sid
  eu_row=$(demo_storefront_row demoEu)
  if [[ -n "$eu_row" ]]; then
    IFS='|' read -r _ eu_sid _ _ <<<"$eu_row"
    product_id=$(pick_sellable_product "$eu_sid" "EUR" 0 "imported-sample")
    if [[ -n "$product_id" ]]; then manifest_set "products.importedSample.id" "$(json_string "$product_id")"; fi
  fi
}

seed_exhaustive() {
  seed_full
  echo "== exhaustive hooks =="
  manifest_append "warnings" "$(json_string "exhaustive historical transaction seeding is planned in qadata_4; qadata_2 only establishes profiles and fixture manifest primitives")"
}

init_manifest
case "$PROFILE" in
  smoke) seed_smoke ;;
  full) seed_full ;;
  exhaustive) seed_exhaustive ;;
  mirror-prod)
    cat >&2 <<'MSG'
mirror-prod is intentionally not implemented yet.
Future flow should restore a sanitized production snapshot or import a prod export artifact,
then run the same owned-service invariants/migrations. No production data is pulled by this script.
MSG
    exit 2
    ;;
esac

# Populate the last few Mission Control tiles a happy-path run leaves at 0 (cancelled / refund-pending /
# past-due / dropship states / failed notification) so every monitor shows data. Best-effort, non-fatal.
case "$PROFILE" in
  full|exhaustive)
    if [[ -x scripts/seed-monitor-demo.sh ]]; then
      GATEWAY="$GATEWAY" TENANT_ID="$TENANT_ID" scripts/seed-monitor-demo.sh \
        || echo "monitor-demo seed skipped/failed (non-fatal)"
    fi ;;
esac

manifest_set "summaryPath" "$(json_string "$SUMMARY")"

echo "== summary =="
echo "Wrote JSONL summary to $SUMMARY"
echo "Wrote fixture manifest to $MANIFEST"
python3 - "$SUMMARY" <<'PY'
import json, sys
counts = {}
for line in open(sys.argv[1], encoding='utf-8'):
    row = json.loads(line)
    counts[row.get('classification', 'unknown')] = counts.get(row.get('classification', 'unknown'), 0) + 1
print('step classifications: ' + ', '.join(f'{k}={v}' for k, v in sorted(counts.items())))
PY
# Checkouts (and the other must_2xx steps) must all succeed. List every one that did not, with its body,
# and fail the seed — after the whole dataset was still seeded — so a short dataset is never silent.
set +e
python3 - "$SUMMARY" <<'PY'
import json, sys
rows = [json.loads(line) for line in open(sys.argv[1], encoding='utf-8')]
checkouts = [r for r in rows if r.get('path') == '/api/ordering/checkout']
ok = sum(1 for r in checkouts if r.get('classification') == 'ok')
print(f'checkouts: {ok}/{len(checkouts)} succeeded')
bad = [r for r in rows if r.get('expectation') == 'must_2xx' and r.get('classification') != 'ok']
for r in rows:
    if r.get('classification') == 'server_error' and r.get('expectation') != 'must_2xx':
        print(f"  warning: best-effort step {r['step']} {r['method']} {r['path']} -> {r['status']}")
if bad:
    print(f'UNEXPECTED failures in required steps: {len(bad)}')
    for r in bad:
        body = (r.get('bodyPreview') or '').replace('\n', ' ')[:300]
        print(f"  !! {r['step']} {r['method']} {r['path']} -> {r['status']}: {body}")
    sys.exit(4)
PY
rc=$?
set -e
if (( rc != 0 )); then
  echo "Seed finished WITH UNEXPECTED FAILURES (exit $rc) — see above and $SUMMARY" >&2
  exit "$rc"
fi
