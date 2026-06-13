#!/usr/bin/env bash
#
# e2e-verify.sh — regression verification for 3commerce.
#
# Runs every end-to-end check exercised while building the project, so after you
# add new features you can confirm nothing previously working has broken.
#
# Usage:
#   scripts/e2e-verify.sh            # automated suites only (fast, deterministic, Docker for Testcontainers)
#   scripts/e2e-verify.sh --live     # ALSO boot the full stack and run live user-journey smoke flows
#   scripts/e2e-verify.sh --live-only
#
# Exit code is non-zero if any check fails.
#
# ─────────────────────────────────────────────────────────────────────────────
# COVERAGE CHECKLIST  (keep in sync — see the "test list" rule in AGENTS.md)
#
# Automated (encoded in test suites / build):
#   A1  Solution builds with 0 warnings (warnings-as-errors)
#   A2  Formatting clean (dotnet format --verify-no-changes)
#   A3  Backend unit + contract tests (Identity hasher/tokens, contract equality)
#   A4  Integration · spine: outbox atomicity, durable redelivery, inbox idempotency
#   A5  Integration · Identity auth: register no-enumeration, logout revocation,
#       /me requires claims, wrong password rejected, reset revokes sessions
#   A6  Integration · Catalog: import ≥10k SKUs, exact search, typo fallback,
#       filters, search p95 < 500ms, hostile-input safety
#   A7  Storefront typecheck (tsc) + production build (next build)
#   A8  No vulnerable NuGet packages
#
# Live full-stack (only with --live; exercises the gateway + storefront paths the
# in-process integration tests do not):
#   L1  Infra healthy: Postgres (6 service DBs) + RabbitMQ
#   L2  All six services report /health/ready
#   L3  Ping-pong spine flows through the gateway to the Notifications worker
#   L4  Gateway blocks internal health routes (/api/*/health* → 404)
#   L5  Register → 202, identical body on repeat (no user enumeration)
#   L6  Verification email token delivered; verify-email succeeds
#   L7  Login sets cookie; /me with cookie → 200, without → 401
#   L8  Saved address create → 201
#   L9  Admin RBAC: customer → 403, admin authorized
#   L10 Catalog import → ≥10k accepted, >0 rejected
#   L11 Search: exact (X-Total-Count), typo fallback, category+attribute filter, detail
#   L12 Search latency p95 < 500ms
#   L13 Logout → 204; password reset → login with new password
#   L14 Storefront SSR: home/search/product render catalog data; /account redirects
# ─────────────────────────────────────────────────────────────────────────────

set -uo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

GATEWAY="http://localhost:8080"
STOREFRONT="http://localhost:3000"
MODE="auto"
[[ "${1:-}" == "--live" ]] && MODE="auto+live"
[[ "${1:-}" == "--live-only" ]] && MODE="live"

PASS=0 FAIL=0
declare -a FAILED=()

pass() { printf '  \033[32m✓\033[0m %s\n' "$1"; PASS=$((PASS+1)); }
fail() { printf '  \033[31m✗ %s\033[0m\n' "$1"; FAIL=$((FAIL+1)); FAILED+=("$1"); }
stage() { printf '\n\033[1m== %s ==\033[0m\n' "$1"; }
# check "<label>" <expected_substring> <command...>  — passes if command output contains substring
check() {
  local label="$1" want="$2"; shift 2
  local out; out="$("$@" 2>&1)"
  if [[ "$out" == *"$want"* ]]; then pass "$label"; else fail "$label (wanted '$want')"; fi
}

# ── Automated suites ─────────────────────────────────────────────────────────
run_automated() {
  stage "A1–A2  Build + format"
  if dotnet build "$ROOT/3commerce.sln" 2>&1 | grep -q '0 Warning(s)'; then pass "A1 build, 0 warnings"; else fail "A1 build/warnings"; fi
  if dotnet format "$ROOT/3commerce.sln" --verify-no-changes >/dev/null 2>&1; then pass "A2 format clean"; else fail "A2 format"; fi

  stage "A3  Backend unit + contract tests"
  if dotnet test "$ROOT/3commerce.sln" --no-build --filter 'Category!=Integration' 2>&1 | grep -q 'Failed: *0'; then pass "A3 unit/contract"; else fail "A3 unit/contract"; fi

  stage "A4–A6  Integration tests (Testcontainers — Docker required)"
  local out; out="$(dotnet test "$ROOT/tests/3commerce.IntegrationTests" --no-build --filter 'Category=Integration' 2>&1)"
  if grep -q 'Failed: *0' <<<"$out"; then
    local n; n="$(grep -oE 'Passed: *[0-9]+' <<<"$out" | grep -oE '[0-9]+' | tail -1)"
    pass "A4–A6 integration ($n passed)"
  else
    fail "A4–A6 integration"; grep -E 'Failed!|\[FAIL\]' <<<"$out" | head -5
  fi

  stage "A7  Storefront typecheck + build"
  if [[ -d "$ROOT/src/Storefront/node_modules" ]]; then
    ( cd "$ROOT/src/Storefront" && npx tsc --noEmit >/dev/null 2>&1 ) && pass "A7a tsc clean" || fail "A7a tsc"
    ( cd "$ROOT/src/Storefront" && npm run build >/dev/null 2>&1 ) && pass "A7b next build" || fail "A7b next build"
  else
    fail "A7 storefront deps missing (run: cd src/Storefront && npm install)"
  fi

  stage "A8  Vulnerable package scan"
  if dotnet list "$ROOT/3commerce.sln" package --vulnerable --include-transitive 2>&1 | grep -q 'has the following vulnerable'; then
    fail "A8 vulnerable packages found"
  else
    pass "A8 no vulnerable packages"
  fi
}

# ── Live full-stack smoke ────────────────────────────────────────────────────
wait_health() { # port
  for _ in $(seq 1 30); do curl -fsS "localhost:$1/health/ready" >/dev/null 2>&1 && return 0; sleep 1; done
  return 1
}

run_live() {
  stage "L1  Infra (Postgres + RabbitMQ)"
  docker compose -f "$ROOT/docker-compose.infra.yml" up -d >/dev/null 2>&1
  for _ in $(seq 1 30); do
    [[ "$(docker exec 3commerce-postgres psql -U postgres -tc '\l' 2>/dev/null | grep -c '_db')" == "6" ]] && break; sleep 1
  done
  check "L1 six service databases" "6" bash -c "docker exec 3commerce-postgres psql -U postgres -tc '\l' | grep -c '_db'"

  stage "Applying migrations"
  for svc in Identity Catalog Ordering Payments Fulfillment Support; do
    dotnet ef database update -p "$ROOT/src/Services/$svc/Infrastructure" -s "$ROOT/src/Services/$svc/Api" >/dev/null 2>&1 \
      && printf '  migrated %s\n' "$svc" || printf '  (migrate %s skipped/failed)\n' "$svc"
  done

  stage "Booting services + storefront"
  dotnet build "$ROOT/3commerce.sln" >/dev/null 2>&1
  : > "$ROOT/.run/notifications.log" 2>/dev/null || true
  "$ROOT/scripts/run-all.sh" start >/dev/null
  ( cd "$ROOT/src/Storefront" && GATEWAY_URL="$GATEWAY" npm run start >/tmp/3c-storefront.log 2>&1 & )
  local ok=1; for p in 5101 5102 5103 5104 5105 5106; do wait_health "$p" || ok=0; done
  [[ $ok == 1 ]] && pass "L2 six services /health/ready" || fail "L2 service health"
  sleep 6  # storefront warmup

  stage "L3–L4  Gateway routing"
  check "L3 ping-pong via gateway → worker" "PONG received" bash -c \
    "curl -fsS -X POST $GATEWAY/api/catalog/ping >/dev/null; sleep 4; grep -a 'PONG received' '$ROOT/.run/notifications.log' | tail -1"
  check "L4 gateway blocks internal health" "404" bash -c \
    "curl -s -o /dev/null -w '%{http_code}' $GATEWAY/api/ordering/health/ready"

  stage "L5–L8  Auth lifecycle"
  local jar=/tmp/3c-e2e-cookies.txt; rm -f "$jar"
  local email="e2e-$(date +%s)@example.com"
  local b1 b2
  b1="$(curl -s -X POST $GATEWAY/api/identity/register -H 'content-type: application/json' -d "{\"email\":\"$email\",\"password\":\"a-strong-password\"}")"
  b2="$(curl -s -X POST $GATEWAY/api/identity/register -H 'content-type: application/json' -d "{\"email\":\"$email\",\"password\":\"a-strong-password\"}")"
  [[ "$b1" == "$b2" && -n "$b1" ]] && pass "L5 register no-enumeration" || fail "L5 register"
  sleep 3
  local token; token="$(grep -aoE 'verify-email\?token=[A-Za-z0-9_-]+' "$ROOT/.run/notifications.log" | tail -1 | cut -d= -f2)"
  check "L6 verify-email" "verified" bash -c \
    "curl -s -X POST $GATEWAY/api/identity/verify-email -H 'content-type: application/json' -d '{\"token\":\"$token\"}'"
  curl -s -c "$jar" -X POST $GATEWAY/api/identity/login -H 'content-type: application/json' -d "{\"email\":\"$email\",\"password\":\"a-strong-password\"}" >/dev/null
  check "L7a /me with cookie → 200" "200" bash -c "curl -s -o /dev/null -w '%{http_code}' -b '$jar' $GATEWAY/api/identity/me"
  check "L7b /me without cookie → 401" "401" bash -c "curl -s -o /dev/null -w '%{http_code}' $GATEWAY/api/identity/me"
  check "L8 add address → 201" "201" bash -c \
    "curl -s -o /dev/null -w '%{http_code}' -b '$jar' -X POST $GATEWAY/api/identity/me/addresses -H 'content-type: application/json' -d '{\"name\":\"E2E\",\"line1\":\"1 St\",\"city\":\"Berlin\",\"postcode\":\"10115\",\"country\":\"DE\"}'"

  stage "L9–L12  Catalog: RBAC, import, search"
  local admin=/tmp/3c-e2e-admin.txt; rm -f "$admin"
  curl -s -c "$admin" -X POST $GATEWAY/api/identity/login -H 'content-type: application/json' -d '{"email":"admin@3commerce.local","password":"dev-admin-password-1"}' >/dev/null
  check "L9a customer → 403 on admin" "403" bash -c "curl -s -o /dev/null -w '%{http_code}' -b '$jar' -X POST $GATEWAY/api/catalog/admin/import-runs"
  local imp; imp="$(curl -s -b "$admin" -X POST $GATEWAY/api/catalog/admin/import-runs)"
  local acc rej; acc="$(grep -oE '"accepted":[0-9]+' <<<"$imp" | grep -oE '[0-9]+')"; rej="$(grep -oE '"rejected":[0-9]+' <<<"$imp" | grep -oE '[0-9]+')"
  { [[ "${acc:-0}" -ge 10000 && "${rej:-0}" -gt 0 ]] && pass "L10 import (${acc} accepted/${rej} rejected)"; } || fail "L10 import (acc=${acc:-?} rej=${rej:-?})"
  check "L11a exact search has total" "X-Total-Count" bash -c "curl -s -D - -o /dev/null '$GATEWAY/api/catalog/products?q=Headphones&pageSize=3'"
  check "L11b typo fallback" "Headphones" bash -c "curl -s '$GATEWAY/api/catalog/products?q=hedphones&pageSize=3'"
  check "L11c category+attr filter ok" "200" bash -c "curl -s -o /dev/null -w '%{http_code}' '$GATEWAY/api/catalog/products?category=audio&attrs=color:black'"
  local slug; slug="$(curl -s "$GATEWAY/api/catalog/products?q=Speaker&pageSize=1" | grep -oE '"slug":"[^"]+"' | head -1 | cut -d'"' -f4)"
  check "L11d product detail" "variants" bash -c "curl -s '$GATEWAY/api/catalog/products/$slug'"
  local p95; p95="$(for i in $(seq 1 30); do curl -s -o /dev/null -w '%{time_total}\n' "$GATEWAY/api/catalog/products?q=wireless+speaker&page=$i"; done | sort -n | awk '{a[NR]=$1} END{print a[int(NR*0.95)]}')"
  awk "BEGIN{exit !($p95 < 0.5)}" && pass "L12 search p95 ${p95}s < 0.5s" || fail "L12 search p95 ${p95}s"

  stage "L13  Logout + password reset"
  check "L13a logout → 204" "204" bash -c "curl -s -o /dev/null -w '%{http_code}' -b '$jar' -X POST $GATEWAY/api/identity/logout"
  curl -s -X POST $GATEWAY/api/identity/password-reset/request -H 'content-type: application/json' -d "{\"email\":\"$email\"}" >/dev/null
  sleep 3
  local rt; rt="$(grep -aoE 'reset-password\?token=[A-Za-z0-9_-]+' "$ROOT/.run/notifications.log" | tail -1 | cut -d= -f2)"
  curl -s -X POST $GATEWAY/api/identity/password-reset/confirm -H 'content-type: application/json' -d "{\"token\":\"$rt\",\"newPassword\":\"brand-new-password-9\"}" >/dev/null
  check "L13b login with new password" "200" bash -c \
    "curl -s -o /dev/null -w '%{http_code}' -X POST $GATEWAY/api/identity/login -H 'content-type: application/json' -d '{\"email\":\"$email\",\"password\":\"brand-new-password-9\"}'"

  stage "L14  Storefront SSR"
  check "L14a home renders products" "</h3>" bash -c "curl -fsS $STOREFRONT/"
  check "L14b search renders" "items" bash -c "curl -fsS '$STOREFRONT/search?q=speaker'"
  check "L14c product detail renders" "<h1" bash -c "curl -fsS '$STOREFRONT/products/$slug'"
  check "L14d account redirects unauth" "307" bash -c "curl -s -o /dev/null -w '%{http_code}' $STOREFRONT/account"

  stage "Tearing down"
  "$ROOT/scripts/run-all.sh" stop >/dev/null 2>&1
  pkill -f 'next-server|npm run start' 2>/dev/null || true
  echo "  services stopped (infra containers left running)"
}

# ── Run ──────────────────────────────────────────────────────────────────────
printf '\033[1m3commerce E2E verification — mode: %s\033[0m\n' "$MODE"
[[ "$MODE" != "live" ]] && run_automated
[[ "$MODE" == *live* ]] && run_live

stage "Summary"
printf '  passed: %d   failed: %d\n' "$PASS" "$FAIL"
if (( FAIL > 0 )); then
  printf '\n  failing checks:\n'; printf '    - %s\n' "${FAILED[@]}"
  exit 1
fi
printf '\n  \033[32mAll checks passed.\033[0m\n'
