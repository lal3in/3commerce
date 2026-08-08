#!/usr/bin/env bash
# Verify the storefront go-live readiness gate end to end against a running DEV stack (ADR-0043).
#
# Proves the cross-service flow the transactional outbox makes possible:
#   1. a Public storefront with a canonical domain, in Preview, CANNOT go live while it has no
#      active payment account (activation is blocked with a clear reason), then
#   2. once a payment account is created + submitted + activated in Payments, that readiness signal
#      is projected into Catalog (via StorefrontPaymentReadinessChanged over the bus outbox) and the
#      same storefront CAN go live.
#
# This is the runtime companion to the StorefrontReadinessCrossServiceTests integration test: it
# catches a regression where a readiness publisher stages its event in the EF bus outbox but never
# flushes it (publish without a following SaveChanges), which would silently block every go-live.
#
# Usage:
#   scripts/verify-golive-gate.sh [--gateway http://localhost:8080]
#
# API-first by design: it only drives public/admin APIs (no direct DB access), so service
# invariants, RLS, outbox, audit, and validation all stay in force. Exits non-zero on any failure.
set -euo pipefail
cd "$(dirname "$0")/.."

GATEWAY="${GATEWAY:-http://localhost:8080}"
TENANT_ID="${TENANT_ID:-00000000-0000-0000-0000-000000000001}"
ADMIN_EMAIL="${ADMIN_EMAIL:-admin@3commerce.local}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-dev-admin-password-1}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --gateway) GATEWAY="$2"; shift 2 ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

JAR="$(mktemp)"
trap 'rm -f "$JAR"' EXIT
JSON='Content-Type: application/json'
fail() { echo "FAIL: $*" >&2; exit 1; }
code() { curl -sS -b "$JAR" -o /dev/null -w '%{http_code}' "$@"; }
body() { curl -sS -b "$JAR" "$@"; }
json_field() { sed -n "s/.*\"$1\":\"\\([^\"]*\\)\".*/\\1/p"; }

# 1. Admin session.
login=$(curl -sS -c "$JAR" -X POST "$GATEWAY/api/identity/login" -H "$JSON" \
  -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}" -o /dev/null -w '%{http_code}')
[[ "$login" == "200" ]] || fail "admin login ($login) — is the dev stack running at $GATEWAY?"
echo "admin login: 200"

# 2. Fresh Public storefront (Draft).
name="GoLiveVerify-$(date +%s)"
sf=$(body -X POST "$GATEWAY/api/catalog/admin/storefronts" -H "$JSON" \
  -d "{\"tenantId\":\"$TENANT_ID\",\"name\":\"$name\",\"visibility\":4,\"currency\":\"EUR\"}")
sfid=$(printf '%s' "$sf" | json_field id)
[[ -n "$sfid" ]] || fail "could not create storefront: $sf"
echo "create storefront: $sfid"

# 3. Canonical domain + preview (domain/visibility readiness satisfied).
dc=$(code -X POST "$GATEWAY/api/catalog/admin/storefronts/$sfid/domains" -H "$JSON" \
  -d "{\"host\":\"golive-verify-$sfid.test\",\"canonical\":true}")
[[ "$dc" == "200" ]] || fail "add canonical domain ($dc)"
pc=$(code -X POST "$GATEWAY/api/catalog/admin/storefronts/$sfid/preview")
[[ "$pc" == "200" ]] || fail "preview transition ($pc)"
echo "domain + preview: 200"

# 4. Activation must be BLOCKED with no active payment account.
blocked=$(body -X POST "$GATEWAY/api/catalog/admin/storefronts/$sfid/activate")
bcode=$(code -X POST "$GATEWAY/api/catalog/admin/storefronts/$sfid/activate")
[[ "$bcode" == "400" ]] || fail "expected activation to be blocked (400), got $bcode: $blocked"
printf '%s' "$blocked" | grep -qi "active payment account" \
  || fail "block reason did not mention the missing payment account: $blocked"
echo "activate (no payment account): 400 — blocked as expected"

# 5. Create + submit + activate a payment account for this storefront (the real Payments path).
pa=$(body -X POST "$GATEWAY/api/payments/admin/payment-accounts" -H "$JSON" \
  -d "{\"tenantId\":\"$TENANT_ID\",\"storefrontId\":\"$sfid\",\"name\":\"GoLiveVerify Acct\",\"provider\":\"stripe\",\"mode\":1,\"isDefaultForStorefront\":true}")
paid=$(printf '%s' "$pa" | json_field id)
[[ -n "$paid" ]] || fail "could not create payment account: $pa"
[[ "$(code -X POST "$GATEWAY/api/payments/admin/payment-accounts/$paid/submit")" == "200" ]] || fail "payment account submit"
[[ "$(code -X POST "$GATEWAY/api/payments/admin/payment-accounts/$paid/activate")" == "200" ]] || fail "payment account activate"
echo "payment account create + submit + activate: 200"

# 6. Poll activation until the readiness signal is projected into Catalog (over the bus outbox).
#    Activation stays 400 until the StorefrontPaymentReadinessChanged event is consumed, then 200.
echo -n "waiting for readiness projection + go-live: "
deadline=$(( $(date +%s) + 30 ))
acode="000"
until [[ "$acode" == "200" ]]; do
  acode=$(code -X POST "$GATEWAY/api/catalog/admin/storefronts/$sfid/activate")
  [[ "$acode" == "200" ]] && break
  [[ "$acode" == "400" ]] || fail "unexpected activation status $acode while waiting"
  (( $(date +%s) < deadline )) || fail "readiness never projected — activation stuck at $acode (outbox likely not flushed)"
  sleep 0.25
done
echo "activate: 200 — went live"

# 7. Confirm the storefront is Active (state 3) via the admin list.
state=$(body "$GATEWAY/api/catalog/admin/storefronts?tenantId=$TENANT_ID" \
  | tr '{' '\n' | grep "\"id\":\"$sfid\"" | sed -n 's/.*"state":\([0-9]*\).*/\1/p')
[[ "$state" == "3" ]] || fail "expected final state Active (3), got '${state:-?}'"

echo
echo "PASS: go-live gate blocks without an active payment account, then allows once Payments projects readiness into Catalog."
