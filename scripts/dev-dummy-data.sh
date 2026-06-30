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
    -h|--help) sed -n '1,36p' "$0"; exit 0 ;;
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
  python3 - "$1" <<'PY'
import json, sys
path = sys.argv[1]
try:
    data = json.load(sys.stdin)
    cur = data
    for part in path.split('.'):
        if not part:
            continue
        if isinstance(cur, list):
            cur = cur[int(part)]
        else:
            cur = cur[part]
    print(cur)
except Exception:
    pass
PY
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
    variant_payload.append({"id": None, "sku": f"E2E-{code.upper()}-{suffix}", "priceMinor": price + (i * 500), "currency": "EUR", "stockQuantity": stock, "weightGrams": 500 + (i * 100) if product_type in ("Physical", "Bundle") else 0, "lengthMm": 200, "widthMm": 150, "heightMm": 100})
payload = {"tenantId": tenant_id, "slug": f"e2e-scenario-{code}", "title": f"E2E Scenario {code}", "brand": "3commerce QA", "description": f"Deterministic QA scenario product for {code}.", "categoryId": category_id, "attributes": {"scenarioCode": code, "productType": product_type, "supplyCategory": supply, "fulfilmentType": fulfilment, "pricingModel": pricing, "billingMode": billing, "qaSeed": "true"}, "imageUrls": [f"https://placehold.co/800x600?text={code}"], "variants": variant_payload}
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
    "subscription-yearly-tiered": ("Service", "ManualService", "Tiered", "Yearly", 9999, [{"fromQuantity":1,"unitPriceMinor":9999},{"fromQuantity":10,"unitPriceMinor":8499}]),
    "usage-api-meter": ("Digital", "Usage", "UsageBased", "Monthly", 0, []),
    "manual-service-onetime": ("Service", "ManualService", "OneTime", "Once", 7500, []),
    "out-of-stock-hold": ("Physical", "Warehouse", "OneTime", "Once", 1899, []),
    "inactive-unpublished-private": ("Physical", "Warehouse", "OneTime", "Once", 2199, []),
}
supply, fulfilment, pricing, period, price, tiers = meta[code]
print(json.dumps({"tenantId": tenant_id, "productId": product_id, "variantId": variant_id or None, "supplierId": supplier_id, "supplyCategory": supply, "fulfilmentType": fulfilment, "priceMinor": price, "currency": "EUR", "priority": 10, "pricingModel": pricing, "billingPeriod": period, "tiers": tiers}))
PY
}

seed_scenario_matrix() {
  local supplier_id="$1" location_id="$2"
  echo "== product/supply/billing scenario matrix =="
  local categories category_id product_search product_id product_body product_json variant_id offer_json offer_body offer_id stock_qty availability_status
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
    [[ -n "$variant_id" ]] && manifest_set "products.$code.variantId" "$(json_string "$variant_id")"

    offer_json=$(scenario_offer_json "$code" "$product_id" "$variant_id" "$supplier_id")
    offer_body=$(api "scenario-$code-offer" POST "/api/catalog/admin/offers" "$ADMIN_JAR" "$offer_json" "allow_4xx")
    offer_id=$(printf '%s' "$offer_body" | json_get id)
    [[ -n "$offer_id" ]] && manifest_set "offers.$code.id" "$(json_string "$offer_id")"

    case "$code" in
      physical-warehouse-flat|physical-multi-variant-tiered|bundle-mixed-physical) stock_qty=25 ;;
      out-of-stock-hold|inactive-unpublished-private) stock_qty=0 ;;
      *) stock_qty=999 ;;
    esac
    if [[ -n "$location_id" && -n "$variant_id" ]]; then
      api "scenario-$code-stock" POST "/api/fulfillment/admin/inventory/stock" "$ADMIN_JAR" "{\"tenantId\":\"$TENANT_ID\",\"locationId\":\"$location_id\",\"productId\":\"$product_id\",\"variantId\":\"$variant_id\",\"onHand\":$stock_qty}" "allow_4xx" >/dev/null
      manifest_set "fulfillment.stock.$code.onHand" "$stock_qty"
    fi
    availability_status="Available"
    [[ "$code" == "inactive-unpublished-private" ]] && availability_status="OutOfStock"
    api "scenario-$code-availability" POST "/api/fulfillment/admin/dropship/availability" "$ADMIN_JAR" "{\"tenantId\":\"$TENANT_ID\",\"supplierId\":\"$supplier_id\",\"productId\":\"$product_id\",\"variantId\":\"$variant_id\",\"status\":\"$availability_status\",\"externalQuantity\":$stock_qty,\"supplierSku\":\"SUP-$code\"}" "allow_4xx" >/dev/null
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

  jar="$OUT_DIR/checkout-$code.cookie"
  cp "$CUSTOMER_JAR" "$jar" 2>/dev/null || true
  api "history-$code-cart-add" POST "/api/ordering/cart/items" "$jar" \
    "{\"productId\":\"$product_id\",\"variantId\":\"$variant_id\",\"quantity\":$quantity}" "allow_4xx" >/dev/null
  order_body=$(api "history-$code-checkout" POST "/api/ordering/checkout" "$jar" \
    "{\"email\":\"$customer_email\",\"shippingAddress\":{\"name\":\"Demo Customer\",\"line1\":\"42 Example Street\",\"city\":\"Melbourne\",\"postcode\":\"3000\",\"country\":\"AU\"},\"selectedShippingService\":\"Fake Ground\",\"selectedShippingAmountMinor\":499,\"selectedShippingExpiresAt\":\"2999-01-01T00:00:00Z\"}" "allow_4xx")
  order_id=$(printf '%s' "$order_body" | json_get orderId)
  client_secret=$(printf '%s' "$order_body" | json_get clientSecret)
  if [[ -z "$order_id" ]]; then
    manifest_append "warnings" "$(json_string "checkout_scenario $code did not return an order id")"
    return
  fi
  intent_id="${client_secret%_secret_test}"
  [[ -z "$intent_id" || "$intent_id" == "$client_secret" ]] && intent_id="pi_fake_${order_id//-/}"
  manifest_set "orders.$code.id" "$(json_string "$order_id")"
  manifest_set "payments.$code.intentId" "$(json_string "$intent_id")"

  api "history-$code-pay" POST "/api/payments/dev/simulate-payment/$intent_id" "$jar" "" "allow_4xx" >/dev/null
  for _ in $(seq 1 20); do
    sleep 1
    status_body=$(api "history-$code-status" GET "/api/ordering/orders/$order_id/status" "$jar" "" "allow_4xx")
    status=$(printf '%s' "$status_body" | json_get status)
    [[ "$status" == "Confirmed" ]] && break
  done
  [[ -n "$status" ]] && manifest_set "orders.$code.status" "$(json_string "$status")"

  ticket_body=$(api "history-$code-ticket" POST "/api/support/tickets" "$jar" \
    "{\"orderId\":\"$order_id\",\"email\":\"$customer_email\",\"reason\":\"Other\",\"message\":\"Demo support thread for $code\"}" "allow_4xx")
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
    if [[ "$code" == "physical-warehouse-flat" ]]; then
      api "history-$code-rma-approve" POST "/api/support/admin/rmas/$rma_id/approve" "$ADMIN_JAR" '{"requireReturn":false}' "allow_4xx" >/dev/null
    elif [[ "$code" == "out-of-stock-hold" ]]; then
      api "history-$code-rma-deny" POST "/api/support/admin/rmas/$rma_id/deny" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    fi
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
        "{\"tenantId\":\"$TENANT_ID\",\"carrier\":\"Fake\"}" "allow_4xx" >/dev/null
      api "history-$code-tracking-refresh" POST "/api/fulfillment/admin/packages/$package_id/tracking/refresh?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    fi
  fi

  subs=$(api "history-$code-subscriptions" GET "/api/payments/admin/subscriptions?tenantId=$TENANT_ID&email=$customer_email" "$ADMIN_JAR" "" "allow_4xx")
  sub_id=$(printf '%s' "$subs" | json_get '0.id')
  [[ -n "$sub_id" ]] && manifest_set "subscriptions.$code.id" "$(json_string "$sub_id")"
}

seed_historical_flows() {
  echo "== historical commerce flows =="
  checkout_scenario "physical-warehouse-flat" 1
  checkout_scenario "physical-dropship-flat" 1
  checkout_scenario "digital-download-onetime" 1
  checkout_scenario "subscription-monthly-flat" 1
  checkout_scenario "usage-api-meter" 3
  checkout_scenario "out-of-stock-hold" 1
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
    '{"label":"Demo Home","purpose":"Both","line1":"42 Example Street","line2":"Unit 3","city":"Melbourne","region":"VIC","postalCode":"3000","countryCode":"AU","isDefault":true}' "allow_4xx" >/dev/null

  manifest_set "customers.demo.email" "$(json_string "$email")"
  manifest_set "customers.demo.password" "$(json_string "$password")"
  for code in "${SCENARIO_CODES[@]}"; do
    manifest_append "scenarioCodes" "$(json_string "$code")"
    manifest_set "products.$code.code" "$(json_string "$code")"
    manifest_set "products.$code.name" "$(json_string "E2E Scenario ${code}")"
  done
}

seed_full() {
  seed_smoke
  echo "== full best-effort operator data =="

  local entity_json entity_id supplier_id location_json location_id carrier_json carrier_id product_json product_id variant_id variant_json
  entity_json=$(api "entity-create-supplier" POST "/api/entity/entities" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"type\":\"Organization\",\"legalName\":\"Demo Supplier Pty Ltd\",\"tradingName\":\"Demo Supplier\",\"roles\":[\"Supplier\"]}" "allow_4xx")
  entity_id=$(printf '%s' "$entity_json" | json_get id)
  if [[ -n "$entity_id" ]]; then
    api "entity-supplier-start" POST "/api/entity/entities/$entity_id/suppliers" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    api "entity-duplicate-scan" POST "/api/entity/entities/$entity_id/duplicate-warnings/scan" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    api "entity-change-request" POST "/api/entity/entities/$entity_id/suppliers/change-requests?tenantId=$TENANT_ID" "$ADMIN_JAR" \
      '{"type":"Contact","summary":"Demo supplier contact update","detail":"Seeded by scripts/dev-dummy-data.sh"}' "allow_4xx" >/dev/null
    supplier_id="$entity_id"
    manifest_set "entities.demoSupplier.id" "$(json_string "$entity_id")"
  else
    supplier_id="00000000-0000-0000-0000-000000000001"
    manifest_append "warnings" "$(json_string "entity-create-supplier did not return an id; using fallback supplier id")"
  fi

  api "marketing-campaign" POST "/api/marketing/admin/campaigns" "$ADMIN_JAR" \
    '{"tenantId":"00000000-0000-0000-0000-000000000001","code":"DEMO10","name":"Demo launch campaign","startsAt":null,"endsAt":null}' "allow_4xx" >/dev/null
  api "pricing-price" POST "/api/pricing/admin/prices" "$ADMIN_JAR" \
    '{"tenantId":"00000000-0000-0000-0000-000000000001","name":"Demo monthly tiered price","pricingModel":"Tiered","billingPeriod":"Monthly","currency":"EUR","amountMinor":1999,"tiers":[{"upToQuantity":10,"unitAmountMinor":1999},{"upToQuantity":100,"unitAmountMinor":1499}]}' "allow_4xx" >/dev/null

  api "payment-account" POST "/api/payments/admin/payment-accounts" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"storefrontId\":null,\"provider\":\"Stripe\",\"providerMode\":\"Test\",\"displayName\":\"Demo Stripe test account\",\"countryCode\":\"AU\",\"currency\":\"EUR\"}" "allow_4xx" >/dev/null
  api "supplier-bank" POST "/api/payments/admin/supplier-payouts/bank-accounts" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"supplierId\":\"$supplier_id\",\"provider\":\"Manual\",\"tokenRef\":\"vault_demo_supplier_bank\",\"maskedAccount\":\"****1234\",\"accountName\":\"Demo Supplier Pty Ltd\",\"countryCode\":\"AU\",\"currency\":\"EUR\"}" "allow_4xx" >/dev/null
  api "xero-mapping" POST "/api/payments/admin/xero/mappings" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"ledgerAccountCode\":\"revenue.sales\",\"xeroAccountCode\":\"200\",\"scope\":\"TenantDefault\",\"storefrontId\":null,\"categoryId\":null,\"supplierId\":null,\"productId\":null}" "allow_4xx" >/dev/null

  location_json=$(api "fulfillment-location" POST "/api/fulfillment/admin/inventory/locations" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"name\":\"Demo warehouse\",\"kind\":\"Warehouse\",\"entityId\":\"$supplier_id\",\"addressId\":null}" "allow_4xx")
  location_id=$(printf '%s' "$location_json" | json_get id)
  [[ -n "$location_id" ]] && manifest_set "fulfillment.locations.demoWarehouse.id" "$(json_string "$location_id")"

  carrier_json=$(api "fulfillment-carrier" POST "/api/fulfillment/admin/carriers" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"storefrontId\":null,\"carrier\":\"Fake\",\"credentialRef\":null}" "allow_4xx")
  carrier_id=$(printf '%s' "$carrier_json" | json_get id)
  if [[ -n "$carrier_id" ]]; then
    api "fulfillment-carrier-activate" POST "/api/fulfillment/admin/carriers/$carrier_id/activate?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    api "fulfillment-carrier-default" POST "/api/fulfillment/admin/carriers/$carrier_id/default?tenantId=$TENANT_ID" "$ADMIN_JAR" "" "allow_4xx" >/dev/null
    manifest_set "fulfillment.carriers.fake.id" "$(json_string "$carrier_id")"
  fi

  api "usage-provision" POST "/api/usage/admin/usage/provision" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"email\":\"usage.demo@example.test\",\"meterCode\":\"api-calls\",\"includedQuantity\":1000,\"overageAllowed\":true,\"overageUnitPriceMinor\":5,\"currency\":\"EUR\"}" "allow_4xx" >/dev/null
  api "usage-record" POST "/api/usage/admin/usage/record" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"email\":\"usage.demo@example.test\",\"meterCode\":\"api-calls\",\"quantity\":42,\"referenceId\":\"dev-dummy-$RUN_ID\"}" "allow_4xx" >/dev/null
  manifest_set "usage.demo.email" '"usage.demo@example.test"'
  manifest_set "usage.demo.meterCode" '"api-calls"'

  seed_scenario_matrix "$supplier_id" "${location_id:-}"
  # Give outbox/bus projections (Catalog -> Ordering/Fulfillment/Support) a short window before
  # driving customer flows that depend on ProductCopy/OfferCopy/OrderSnapshot read models.
  sleep 5
  seed_historical_flows

  product_json=$(api "catalog-products" GET "/api/catalog/admin/products?tenantId=$TENANT_ID&pageSize=1" "$ADMIN_JAR" "" "allow_4xx")
  product_id=$(printf '%s' "$product_json" | json_get '0.id')
  variant_id=$(printf '%s' "$product_json" | json_get '0.variants.0.id')
  if [[ -n "$product_id" ]]; then
    if [[ -n "$variant_id" ]]; then variant_json="\"$variant_id\""; else variant_json="null"; fi
    api "catalog-offer" POST "/api/catalog/admin/offers" "$ADMIN_JAR" \
      "{\"tenantId\":\"$TENANT_ID\",\"productId\":\"$product_id\",\"variantId\":$variant_json,\"supplierId\":\"$supplier_id\",\"supplyCategory\":\"Physical\",\"fulfilmentType\":\"Dropship\",\"billingMode\":\"OneTime\",\"priceMinor\":2499,\"currency\":\"EUR\",\"priority\":10,\"isActive\":true}" "allow_4xx" >/dev/null
    manifest_set "products.importedSample.id" "$(json_string "$product_id")"
    [[ -n "$variant_id" ]] && manifest_set "products.importedSample.variantId" "$(json_string "$variant_id")"
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
