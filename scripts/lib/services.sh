# Canonical service manifest — the ONE place the service list lives.
# Sourced by run-all.sh, dev-up.sh, dev-down.sh, build-images.sh.
# Add a service here (+ its config/migration) and every script picks it up; no per-script drift.
#
# Format: "name:relative/project/path:httpPort"   (port empty = no HTTP, e.g. a worker)

# DB-owning services (have an EF Infrastructure project under src/Services/<Pascal>).
SERVICES=(
  "identity:src/Services/Identity/Api:5101"
  "catalog:src/Services/Catalog/Api:5102"
  "ordering:src/Services/Ordering/Api:5103"
  "payments:src/Services/Payments/Api:5104"
  "fulfillment:src/Services/Fulfillment/Api:5105"
  "support:src/Services/Support/Api:5106"
  "entity:src/Services/Entity/Api:5107"
  "marketing:src/Services/Marketing/Api:5108"
  "pricing:src/Services/Pricing/Api:5109"
  "audit:src/Services/Audit/Api:5110"
  "workflow:src/Services/Workflow/Api:5111"
  "entitlement:src/Services/Entitlement/Api:5112"
  "usage:src/Services/Usage/Api:5113"
)

# Gateway + workers. notifications is a worker that ALSO owns a small delivery-log DB and exposes a
# read-only admin surface (mc_proc_4), so it runs as an HTTP host on 5114; it self-applies its
# migrations at startup (it is not in the SERVICES ef-migrate loop below).
EDGE=(
  "gateway:src/Gateway:8080"
  "notifications:src/Workers/Notifications:5114"
)

# PascalCase service folders that have EF migrations (derive from SERVICES paths).
ef_projects() {
  local entry name
  for entry in "${SERVICES[@]}"; do
    name="${entry#*src/Services/}"; name="${name%%/*}"
    echo "$name"
  done
}
