#!/usr/bin/env bash
# Seed a running DEV stack with broad, realistic demo data through public/admin APIs.
#
# Usage:
#   scripts/dev-dummy-data.sh [--gateway http://localhost:8080] [--profile core|full|mirror-prod]
#
# Profiles:
#   core        catalog import + demo shoppers + addresses
#   full        core + best-effort tenant/operator data across Entity, Marketing, Pricing,
#               Payments, Fulfillment, Entitlement, and Usage admin APIs
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

while [[ $# -gt 0 ]]; do
  case "$1" in
    --gateway) GATEWAY="$2"; shift 2 ;;
    --profile) PROFILE="$2"; shift 2 ;;
    --tenant-id) TENANT_ID="$2"; shift 2 ;;
    --admin-email) ADMIN_EMAIL="$2"; shift 2 ;;
    --admin-password) ADMIN_PASSWORD="$2"; shift 2 ;;
    -h|--help) sed -n '1,32p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

mkdir -p "$OUT_DIR"
ADMIN_JAR="$OUT_DIR/admin.cookie"
CUSTOMER_JAR="$OUT_DIR/customer.cookie"
SUMMARY="$OUT_DIR/summary.jsonl"
: > "$SUMMARY"

need() { command -v "$1" >/dev/null 2>&1 || { echo "Missing required command: $1" >&2; exit 1; }; }
need curl
need python3

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
  local name="$1" method="$2" path="$3" code="$4" body_file="$5"
  python3 - "$name" "$method" "$path" "$code" "$body_file" >> "$SUMMARY" <<'PY'
import json, sys
name, method, path, code, body_file = sys.argv[1:]
body = ''
try:
    body = open(body_file, encoding='utf-8').read()[:800]
except Exception:
    pass
print(json.dumps({"step": name, "method": method, "path": path, "status": code, "bodyPreview": body}))
PY
}

api() {
  local name="$1" method="$2" path="$3" jar="$4" data="${5:-}"
  local body_file="$OUT_DIR/${name//[^A-Za-z0-9_.-]/_}.json"
  local code
  if [[ -n "$data" ]]; then
    code=$(curl -sS -k -b "$jar" -c "$jar" -X "$method" "$GATEWAY$path" \
      -H 'content-type: application/json' -d "$data" -o "$body_file" -w '%{http_code}' || true)
  else
    code=$(curl -sS -k -b "$jar" -c "$jar" -X "$method" "$GATEWAY$path" \
      -o "$body_file" -w '%{http_code}' || true)
  fi
  record "$name" "$method" "$path" "$code" "$body_file"
  printf '  %-36s %s %s -> %s\n' "$name" "$method" "$path" "$code" >&2
  cat "$body_file"
}

api_noauth() {
  local name="$1" method="$2" path="$3" jar="$4" data="${5:-}"
  api "$name" "$method" "$path" "$jar" "$data"
}

login_admin() {
  rm -f "$ADMIN_JAR"
  local code
  code=$(curl -sS -k -c "$ADMIN_JAR" -X POST "$GATEWAY/api/identity/login" \
    -H 'content-type: application/json' \
    -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}" \
    -o "$OUT_DIR/admin-login.json" -w '%{http_code}')
  record "admin-login" "POST" "/api/identity/login" "$code" "$OUT_DIR/admin-login.json"
  [[ "$code" == "200" ]] || { echo "Admin login failed ($code). Is the dev stack running?" >&2; exit 1; }
}

seed_core() {
  echo "== core demo data =="
  login_admin
  api "catalog-import" POST "/api/catalog/admin/import-runs" "$ADMIN_JAR" >/dev/null

  local email="demo.customer.$(date +%s)@example.test"
  local password="Demo-password-123"
  api_noauth "register-customer" POST "/api/identity/register" "$CUSTOMER_JAR" \
    "{\"email\":\"$email\",\"password\":\"$password\"}" >/dev/null
  api_noauth "login-customer" POST "/api/identity/login" "$CUSTOMER_JAR" \
    "{\"email\":\"$email\",\"password\":\"$password\"}" >/dev/null
  api "customer-profile" PUT "/api/identity/me" "$CUSTOMER_JAR" \
    '{"givenName":"Demo","familyName":"Customer"}' >/dev/null
  api "customer-address-shipping" POST "/api/identity/me/addresses" "$CUSTOMER_JAR" \
    '{"label":"Demo Home","purpose":"Both","line1":"42 Example Street","line2":"Unit 3","city":"Melbourne","region":"VIC","postalCode":"3000","countryCode":"AU","isDefault":true}' >/dev/null
}

seed_full() {
  seed_core
  echo "== full best-effort operator data =="

  local entity_json entity_id supplier_id product_json product_id variant_id variant_json
  entity_json=$(api "entity-create-supplier" POST "/api/entity/entities" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"type\":\"Organization\",\"legalName\":\"Demo Supplier Pty Ltd\",\"tradingName\":\"Demo Supplier\",\"roles\":[\"Supplier\"]}")
  entity_id=$(printf '%s' "$entity_json" | json_get id)
  if [[ -n "$entity_id" ]]; then
    api "entity-supplier-start" POST "/api/entity/entities/$entity_id/suppliers" "$ADMIN_JAR" >/dev/null
    api "entity-duplicate-scan" POST "/api/entity/entities/$entity_id/duplicate-warnings/scan" "$ADMIN_JAR" >/dev/null
    api "entity-change-request" POST "/api/entity/entities/$entity_id/suppliers/change-requests?tenantId=$TENANT_ID" "$ADMIN_JAR" \
      '{"type":"Contact","summary":"Demo supplier contact update","detail":"Seeded by scripts/dev-dummy-data.sh"}' >/dev/null
    supplier_id="$entity_id"
  else
    supplier_id="00000000-0000-0000-0000-000000000001"
  fi

  api "marketing-campaign" POST "/api/marketing/admin/campaigns" "$ADMIN_JAR" \
    '{"tenantId":"00000000-0000-0000-0000-000000000001","code":"DEMO10","name":"Demo launch campaign","startsAt":null,"endsAt":null}' >/dev/null
  api "pricing-price" POST "/api/pricing/admin/prices" "$ADMIN_JAR" \
    '{"tenantId":"00000000-0000-0000-0000-000000000001","name":"Demo monthly tiered price","pricingModel":"Tiered","billingPeriod":"Monthly","currency":"EUR","amountMinor":1999,"tiers":[{"upToQuantity":10,"unitAmountMinor":1999},{"upToQuantity":100,"unitAmountMinor":1499}]}' >/dev/null

  api "payment-account" POST "/api/payments/admin/payment-accounts" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"storefrontId\":null,\"provider\":\"Stripe\",\"providerMode\":\"Test\",\"displayName\":\"Demo Stripe test account\",\"countryCode\":\"AU\",\"currency\":\"EUR\"}" >/dev/null
  api "supplier-bank" POST "/api/payments/admin/supplier-payouts/bank-accounts" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"supplierId\":\"$supplier_id\",\"provider\":\"Manual\",\"tokenRef\":\"vault_demo_supplier_bank\",\"maskedAccount\":\"****1234\",\"accountName\":\"Demo Supplier Pty Ltd\",\"countryCode\":\"AU\",\"currency\":\"EUR\"}" >/dev/null
  api "xero-mapping" POST "/api/payments/admin/xero/mappings" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"ledgerAccountCode\":\"revenue.sales\",\"xeroAccountCode\":\"200\",\"scope\":\"TenantDefault\",\"storefrontId\":null,\"categoryId\":null,\"supplierId\":null,\"productId\":null}" >/dev/null

  api "fulfillment-location" POST "/api/fulfillment/admin/inventory/locations" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"name\":\"Demo warehouse\",\"kind\":\"Warehouse\",\"entityId\":null,\"countryCode\":\"AU\"}" >/dev/null
  api "fulfillment-carrier" POST "/api/fulfillment/admin/carriers" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"carrier\":\"Fake\",\"displayName\":\"Demo fake carrier\",\"credentialRef\":\"dev/fake\",\"isDefault\":true}" >/dev/null

  api "usage-provision" POST "/api/usage/admin/usage/provision" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"email\":\"usage.demo@example.test\",\"meterCode\":\"api-calls\",\"includedQuantity\":1000,\"overageAllowed\":true,\"overageUnitPriceMinor\":5,\"currency\":\"EUR\"}" >/dev/null
  api "usage-record" POST "/api/usage/admin/usage/record" "$ADMIN_JAR" \
    "{\"tenantId\":\"$TENANT_ID\",\"email\":\"usage.demo@example.test\",\"meterCode\":\"api-calls\",\"quantity\":42,\"referenceId\":\"dev-dummy-$(date +%s)\"}" >/dev/null

  # Try to create an offer against the first catalog product if the import/projection is ready.
  product_json=$(api "catalog-products" GET "/api/catalog/admin/products?tenantId=$TENANT_ID&pageSize=1" "$ADMIN_JAR")
  product_id=$(printf '%s' "$product_json" | json_get '0.id')
  variant_id=$(printf '%s' "$product_json" | json_get '0.variants.0.id')
  if [[ -n "$product_id" ]]; then
    if [[ -n "$variant_id" ]]; then variant_json="\"$variant_id\""; else variant_json="null"; fi
    api "catalog-offer" POST "/api/catalog/admin/offers" "$ADMIN_JAR" \
      "{\"tenantId\":\"$TENANT_ID\",\"productId\":\"$product_id\",\"variantId\":$variant_json,\"supplierId\":\"$supplier_id\",\"supplyCategory\":\"Physical\",\"fulfilmentType\":\"Dropship\",\"billingMode\":\"OneTime\",\"priceMinor\":2499,\"currency\":\"EUR\",\"priority\":10,\"isActive\":true}" >/dev/null
  fi
}

case "$PROFILE" in
  core) seed_core ;;
  full) seed_full ;;
  mirror-prod)
    cat >&2 <<'MSG'
mirror-prod is intentionally not implemented yet.
Future flow should restore a sanitized production snapshot or import a prod export artifact,
then run the same owned-service invariants/migrations. No production data is pulled by this script.
MSG
    exit 2
    ;;
  *) echo "Unknown profile: $PROFILE (expected core|full|mirror-prod)" >&2; exit 2 ;;
esac

echo "== summary =="
echo "Wrote JSONL summary to $SUMMARY"
python3 - "$SUMMARY" <<'PY'
import json, sys
ok = warn = 0
for line in open(sys.argv[1], encoding='utf-8'):
    row = json.loads(line)
    code = row.get('status', '')
    if code.startswith(('2','3','4')):
        ok += 1
    else:
        warn += 1
print(f"steps recorded: {ok + warn}; non-http/failed curl steps: {warn}")
PY
