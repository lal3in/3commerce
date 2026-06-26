# Host targets for scripts/host-check.sh — where the stack can run, beyond localhost.
# Each entry: "name|transport|detail"
#   local              — this machine
#   ssh|user@host[:port]   — any VPS / cloud VM with SSH (Hostinger, EC2, GCE, Azure VM, …)
#   gcp|zone/instance      — Google Compute Engine via `gcloud compute ssh` (synchronous)
# AWS/Azure VMs: use the `ssh` transport with the instance's IP — it's the universal path.
#
# Add yours here, then: scripts/host-check.sh <name>   (or `all`).
HOST_TARGETS=(
  "local|local|"
  # "vps|ssh|deploy@203.0.113.10:22"          # e.g. Hostinger VPS
  # "gce|gcp|us-central1-a/3commerce-app"      # Google Compute Engine
  # "ec2|ssh|ec2-user@203.0.113.20"            # AWS EC2 (or use SSM, see provider_logs)
  # "azvm|ssh|azureuser@203.0.113.30"          # Azure VM
)

# Optional provider-native (managed) log sources — pulled by host-check.sh --logs when configured.
# Leave empty to skip. These need the provider CLI installed + authenticated.
: "${CW_LOG_GROUP:=}"     # AWS CloudWatch Logs group, e.g. /3commerce/prod   (needs `aws`)
: "${GCP_PROJECT:=}"      # GCP project id for Cloud Logging                  (needs `gcloud`)
: "${AZ_WORKSPACE:=}"     # Azure Log Analytics workspace id                  (needs `az`)
