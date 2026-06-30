#!/usr/bin/env bash
# Tear down the bare-run local env (counterpart to dev-up.sh).
set -uo pipefail
cd "$(dirname "$0")/.."
scripts/run-all.sh stop || true
pkill -f "next dev -p 3000" 2>/dev/null || true
pkill -f "3commerce.Admin" 2>/dev/null || true
pkill -f "3commerce.SupplierPortal" 2>/dev/null || true
docker compose -f docker-compose.infra.yml down
echo "down"
