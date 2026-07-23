#!/usr/bin/env bash
# Seeds the last few Mission Control tiles that a normal happy-path run leaves at 0, so an operator can
# SEE every monitor work. Two kinds:
#   • Real gateway flows (portable, no docker): a Cancelled order, and a stuck Refund-pending RMA.
#   • Read-model demo rows via psql (needs the dev Postgres container): a Past-due subscription, the
#     three dropship states (Open/Tracking/Failed), and a Failed notification delivery — states the
#     offline mock fakes never produce on their own. All guards are idempotent (safe to re-run).
# Usage: scripts/seed-monitor-demo.sh   (env: GATEWAY, PG_CONTAINER, TENANT_ID)
set -uo pipefail
GATEWAY="${GATEWAY:-http://localhost:8080}"
PG_CONTAINER="${PG_CONTAINER:-3commerce-postgres}"
TENANT_ID="${TENANT_ID:-00000000-0000-0000-0000-000000000001}"
JAR="$(mktemp)"; trap 'rm -f "$JAR"' EXIT

say() { printf '  %-32s %s\n' "$1" "$2"; }
jget() { python3 -c "import sys,json; d=json.load(sys.stdin); print($1)" 2>/dev/null; }

echo "== monitor demo seed =="

# ---- admin session (also satisfies the customer policy for cart/checkout) ----
code=$(curl -s -c "$JAR" -X POST "$GATEWAY/api/identity/login" -H 'content-type: application/json' \
  -d '{"email":"admin@3commerce.local","password":"dev-admin-password-1"}' -o /dev/null -w '%{http_code}')
[[ "$code" == 2* ]] || { echo "admin login failed ($code) — is the stack up?"; exit 1; }

# ---- (1) Cancelled order: check out WITHOUT paying, then admin-cancel while AwaitingPayment ----
pid=$(curl -s -b "$JAR" "$GATEWAY/api/catalog/products?pageSize=1" | jget "d[0]['id']")
if [[ -n "$pid" ]]; then
  curl -s -b "$JAR" -X POST "$GATEWAY/api/ordering/cart/items" -H 'content-type: application/json' \
    -d "{\"productId\":\"$pid\",\"quantity\":1}" -o /dev/null
  oid=$(curl -s -b "$JAR" -X POST "$GATEWAY/api/ordering/checkout" -H 'content-type: application/json' \
    -d '{"email":"cancel-demo@example.com","shippingAddress":{"name":"Cancel Demo","line1":"1 St","city":"Berlin","postcode":"10115","country":"DE"}}' | jget "d['orderId']")
  if [[ -n "$oid" ]]; then
    sleep 3 # let the saga reach AwaitingPayment
    cc=$(curl -s -b "$JAR" -X POST "$GATEWAY/api/ordering/admin/orders/$oid/cancel" -H 'content-type: application/json' \
      -d '{"reason":"demo — abandoned before payment"}' -o /dev/null -w '%{http_code}')
    say "cancelled order" "$oid ($cc)"
  fi
fi

# ---- (2) Stuck Refund-pending: open an auto-approved RMA on an already-Refunded order. The refund
#          exceeds the remaining balance (0), so ExecuteRefundConsumer rejects it and never emits
#          RefundCompleted — the RMA legitimately parks in RefundPending (a real dunning/stuck case). ----
refunded=$(curl -s -b "$JAR" "$GATEWAY/api/ordering/admin/orders" | jget "next((o['id'] for o in d if o['status']=='Refunded'), '')")
if [[ -n "$refunded" ]]; then
  st=$(curl -s -b "$JAR" -X POST "$GATEWAY/api/support/admin/rmas" -H 'content-type: application/json' \
    -H "Idempotency-Key: refundpending-demo-$refunded" \
    -d "{\"orderId\":\"$refunded\",\"reason\":\"demo — refund exceeds remaining\",\"autoApprove\":true}" | jget "d.get('state','')")
  say "refund-pending RMA on" "$refunded ($st)"
else
  say "refund-pending RMA" "skipped (no Refunded order yet)"
fi

# ---- psql demo rows (needs the dev Postgres container) ----
if ! command -v docker >/dev/null 2>&1 || ! docker ps --format '{{.Names}}' | grep -qx "$PG_CONTAINER"; then
  echo "  (psql demo rows skipped — container '$PG_CONTAINER' not reachable)"
  echo "== monitor demo seed done (flows only) =="; exit 0
fi
psql() { docker exec -i "$PG_CONTAINER" psql -U postgres -d "$1" -v ON_ERROR_STOP=1 -qtA -c "$2"; }

# (3) Past-due subscription: the mock rail never declines, so flip one Active subscription to PastDue.
psql payments_db "UPDATE payments.\"Subscriptions\" SET \"Status\"='PastDue', \"UpdatedAt\"=now()
  WHERE \"Id\"=(SELECT \"Id\" FROM payments.\"Subscriptions\" WHERE \"Status\"='Active' ORDER BY \"CreatedAt\" LIMIT 1)
    AND NOT EXISTS (SELECT 1 FROM payments.\"Subscriptions\" WHERE \"Status\"='PastDue');" >/dev/null && say "past-due subscription" "ok"

# (4) Dropship trio (Open=Accepted / TrackingReceived / Failed) — the fake supplier provider only ever
#     yields TrackingReceived, so seed one of each under the default tenant.
psql fulfillment_db "INSERT INTO fulfillment.\"SupplierOrders\"
  (\"Id\",\"TenantId\",\"OrderId\",\"SupplierId\",\"Status\",\"ExternalReference\",\"TrackingNumber\",\"Carrier\",\"FailureReason\",\"CreatedAt\",\"UpdatedAt\")
  SELECT gen_random_uuid(), '$TENANT_ID', gen_random_uuid(), gen_random_uuid(), s.status,
         s.ext::text, s.trk::text, s.car::text, s.fail::text, now(), now()
  FROM (VALUES
    ('Accepted','EXT-DEMO-ACC', NULL, NULL, NULL),
    ('TrackingReceived','EXT-DEMO-TRK','FDSDEMO000TRACK','FakeDropshipCarrier', NULL),
    ('Failed', NULL, NULL, NULL, 'Supplier rejected the order (demo).')
  ) AS s(status,ext,trk,car,fail)
  WHERE NOT EXISTS (SELECT 1 FROM fulfillment.\"SupplierOrders\" WHERE \"TenantId\"='$TENANT_ID');" >/dev/null && say "dropship trio" "ok"

# (5) Failed notification delivery (Status 2 = Failed) — the dev sender always succeeds.
psql notifications_db "INSERT INTO notifications.deliveries
  (\"Id\",\"Channel\",\"Recipient\",\"Subject\",\"Status\",\"Error\",\"OccurredAt\")
  SELECT gen_random_uuid(),'email','o***@demo.invalid','Order receipt (demo bounce)',2,'SMTP 550: mailbox unavailable (demo)',now()
  WHERE NOT EXISTS (SELECT 1 FROM notifications.deliveries WHERE \"Status\"=2);" >/dev/null && say "failed notification" "ok"

echo "== monitor demo seed done =="
