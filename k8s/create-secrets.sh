#!/usr/bin/env bash
# Create/refresh the AgriForecast Kubernetes secrets (agri-db, agri-jwt,
# agri-ml-key, and the OPTIONAL agri-smtp) in the `agriforecast` namespace.
#
# Sources of truth:
#   - DB creds + ML admin key: the gitignored src/AgriForecast.ML/.env
#     (AGRI_DB_HOST/PORT/NAME/USER/PASSWORD, ML_ADMIN_API_KEY).
#   - JWT signing key: the JWT_KEY environment variable if set, otherwise a
#     hidden interactive prompt (it currently lives in .NET user-secrets:
#       dotnet user-secrets list --project src/AgriForecast.API   # run it
#       yourself to copy the value; this script never reads user-secrets).
#   - SMTP account for the nightly pipeline alert email: OPTIONAL and SKIPPABLE.
#     Press Enter at the prompt to leave it out; the API then logs
#     "sentinel disabled: Smtp not configured" and runs exactly as before.
#
# Idempotent: safe to re-run after any credential rotation.
# SECURITY: this script never echoes a secret value and writes no files.
set -euo pipefail

NAMESPACE=agriforecast
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ENV_FILE="$REPO_DIR/src/AgriForecast.ML/.env"

if ! command -v kubectl >/dev/null 2>&1; then
  echo "ERROR: kubectl not found. Enable Kubernetes in Docker Desktop first." >&2
  exit 1
fi

if [ ! -f "$ENV_FILE" ]; then
  echo "ERROR: $ENV_FILE not found (it is gitignored; it must exist locally)." >&2
  exit 1
fi

# Load the .env in a subshell-safe way: export everything it defines.
set -a
# shellcheck disable=SC1090
. "$ENV_FILE"
set +a

# Validate the keys we depend on (a rotation that drops a line must fail
# loudly here, not as a confusing in-cluster auth error later).
for var in AGRI_DB_HOST AGRI_DB_PORT AGRI_DB_NAME AGRI_DB_USER AGRI_DB_PASSWORD; do
  eval "val=\${$var:-}"
  if [ -z "$val" ]; then
    echo "ERROR: $var missing from $ENV_FILE" >&2
    exit 1
  fi
done
unset var val

# ML admin key: prefer the .env value; fall back to env var; else prompt.
ML_KEY="${ML_ADMIN_API_KEY:-}"
if [ -z "$ML_KEY" ]; then
  printf 'ML_ADMIN_API_KEY not in .env — enter it (input hidden): '
  read -rs ML_KEY
  echo ""
fi
if [ -z "$ML_KEY" ]; then
  echo "ERROR: ML admin key is empty." >&2
  exit 1
fi

# JWT key: env var JWT_KEY, else prompt (hidden).
JWT="${JWT_KEY:-}"
if [ -z "$JWT" ]; then
  printf 'Jwt signing key (>=32 bytes; from dotnet user-secrets; input hidden): '
  read -rs JWT
  echo ""
fi
if [ "${#JWT}" -lt 32 ]; then
  echo "ERROR: Jwt key must be at least 32 bytes (got ${#JWT}); the API refuses shorter keys at startup." >&2
  exit 1
fi

# Namespace must exist before secrets can land in it.
kubectl apply -f "$SCRIPT_DIR/namespace.yaml" >/dev/null

# create ... --dry-run=client -o yaml | apply  ==> idempotent create-or-update.
# AGRI_MODEL_HMAC_KEY is optional (strong model-integrity verification,
# registry.py): propagate it when .env has it, so a future re-signing of the
# registry doesn't silently downgrade the cluster pod to sha256-only checks.
HMAC_ARGS=()
if [ -n "${AGRI_MODEL_HMAC_KEY:-}" ]; then
  HMAC_ARGS=(--from-literal=AGRI_MODEL_HMAC_KEY="$AGRI_MODEL_HMAC_KEY")
fi

kubectl create secret generic agri-db -n "$NAMESPACE" \
  --from-literal=AGRI_DB_HOST="$AGRI_DB_HOST" \
  --from-literal=AGRI_DB_PORT="$AGRI_DB_PORT" \
  --from-literal=AGRI_DB_NAME="$AGRI_DB_NAME" \
  --from-literal=AGRI_DB_USER="$AGRI_DB_USER" \
  --from-literal=AGRI_DB_PASSWORD="$AGRI_DB_PASSWORD" \
  "${HMAC_ARGS[@]+"${HMAC_ARGS[@]}"}" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null
if [ ${#HMAC_ARGS[@]} -gt 0 ]; then
  echo "secret agri-db: created/updated (6 keys, incl. AGRI_MODEL_HMAC_KEY)"
else
  echo "secret agri-db: created/updated (5 keys)"
fi

kubectl create secret generic agri-jwt -n "$NAMESPACE" \
  --from-literal=JWT_KEY="$JWT" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null
echo "secret agri-jwt: created/updated"

kubectl create secret generic agri-ml-key -n "$NAMESPACE" \
  --from-literal=ML_ADMIN_API_KEY="$ML_KEY" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null
echo "secret agri-ml-key: created/updated"

# --- agri-smtp (OPTIONAL) ---------------------------------------------------
# The nightly pipeline sentinel emails the owner when a night was not green.
# It is entirely opt-in: forecast-api.yaml marks every Smtp__* env as
# `optional: true`, so a missing agri-smtp secret leaves the pod perfectly
# healthy and the sentinel simply logs one "disabled" line at startup.
#
# GMAIL: the value below is an APP PASSWORD, not the account password —
# myaccount.google.com -> Security -> 2-Step Verification -> App passwords.
# Google rejects the account password for SMTP.
SMTP_USER="${SMTP_USER:-}"
if [ -z "$SMTP_USER" ]; then
  printf 'Gmail address for pipeline alerts (Enter to SKIP email alerts): '
  # `|| true` on every SMTP prompt: read returns 1 at EOF, and under `set -e` that would abort the
  # script AFTER the three real secrets were already written — a non-interactive run (CI, a piped
  # invocation) must skip email cleanly, not exit 1 having half-finished.
  read -r SMTP_USER || true
fi

if [ -z "$SMTP_USER" ]; then
  echo "secret agri-smtp: skipped (no address given) — pipeline email alerts stay OFF"
else
  SMTP_PASSWORD="${SMTP_PASSWORD:-}"
  if [ -z "$SMTP_PASSWORD" ]; then
    printf 'Gmail APP PASSWORD for %s (16 chars, input hidden): ' "$SMTP_USER"
    read -rs SMTP_PASSWORD || true
    echo ""
  fi
  if [ -z "$SMTP_PASSWORD" ]; then
    echo "ERROR: an address was given but the app password is empty. Re-run and either" >&2
    echo "       supply both, or press Enter at the address prompt to skip email." >&2
    exit 1
  fi

  # Recipient defaults to the sending account — alerting yourself is the normal
  # case. A comma-separated list is accepted (MailMessage splits it).
  SMTP_TO="${SMTP_TO:-}"
  if [ -z "$SMTP_TO" ]; then
    printf 'Send alerts TO (Enter for %s): ' "$SMTP_USER"
    read -r SMTP_TO || true
  fi
  [ -z "$SMTP_TO" ] && SMTP_TO="$SMTP_USER"

  kubectl create secret generic agri-smtp -n "$NAMESPACE" \
    --from-literal=SMTP_USER="$SMTP_USER" \
    --from-literal=SMTP_FROM="$SMTP_USER" \
    --from-literal=SMTP_TO="$SMTP_TO" \
    --from-literal=SMTP_PASSWORD="$SMTP_PASSWORD" \
    --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  echo "secret agri-smtp: created/updated (4 keys) — pipeline email alerts ON"
  unset SMTP_PASSWORD
fi

unset JWT ML_KEY AGRI_DB_PASSWORD

echo ""
echo "Secrets are in place in namespace '$NAMESPACE'."
echo "Running deployments do NOT pick up secret changes automatically — after a"
echo "rotation, restart them:"
echo "  kubectl rollout restart deployment/ml-serving deployment/forecast-api -n $NAMESPACE"
