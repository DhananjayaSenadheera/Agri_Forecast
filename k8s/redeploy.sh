#!/usr/bin/env bash
# Rebuild AgriForecast image(s) and roll them into the cluster.
#
#   ./k8s/redeploy.sh api          # agriforecast-api        -> rollout restart forecast-api
#   ./k8s/redeploy.sh ml           # agriforecast-ml         -> rollout restart ml-serving
#   ./k8s/redeploy.sh ingestion    # agriforecast-ingestion  (CronJob-only image)
#   ./k8s/redeploy.sh all
#
# Why this works without a registry: Docker Desktop Kubernetes shares the
# host Docker image store, and every manifest uses imagePullPolicy:
# IfNotPresent on the :latest tag — so `docker build` updates the image in
# place and (a) a rollout restart picks it up for Deployments, (b) the next
# CronJob run picks it up automatically for pipeline images.
#
# RECOVERY CONTAINERS: the owner keeps two pre-created Docker Desktop
# containers (agriforecast-daily-1-ingest, agriforecast-daily-2-process) as a
# tap-to-run manual recovery path for the daily pipeline. `docker create`
# snapshots the image AT CREATE TIME, so after a rebuild they would silently
# keep running the OLD build. This script therefore recreates any that exist
# and reference a rebuilt image, preserving their exact name/env/entrypoint/
# cmd via `docker inspect` (values are never printed).
#
# Idempotent: safe to re-run; each step states what it did.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SRC_DIR="$REPO_DIR/src"
NAMESPACE=agriforecast

usage() {
  echo "usage: $0 api|ml|ingestion|all" >&2
  exit 2
}

[ $# -eq 1 ] || usage
TARGET="$1"
case "$TARGET" in
  api|ml|ingestion|all) ;;
  *) usage ;;
esac

command -v docker >/dev/null 2>&1 || { echo "ERROR: docker not found" >&2; exit 1; }

# kubectl is optional-ish: builds still make sense without a cluster up, but
# rollouts will be skipped with a warning.
KUBECTL_OK=true
if ! command -v kubectl >/dev/null 2>&1 || ! kubectl get ns "$NAMESPACE" >/dev/null 2>&1; then
  KUBECTL_OK=false
fi

# Recreate a pre-created recovery container in place, preserving its exact
# config (image/env/entrypoint/cmd). Uses python3 + the docker SDK-free CLI:
# config is read as JSON and re-issued as a `docker create` argv list, so no
# secret env value is ever echoed or shell-interpolated.
recreate_recovery_container() {
  local cname="$1" image="$2"
  if ! docker inspect "$cname" >/dev/null 2>&1; then
    return 0 # container not set up on this machine — nothing to do
  fi
  local cimage
  cimage="$(docker inspect -f '{{.Config.Image}}' "$cname")"
  if [ "$cimage" != "$image" ]; then
    return 0 # uses a different image — untouched by this rebuild
  fi
  echo "  recreating recovery container $cname against the fresh $image ..."
  docker inspect "$cname" | python3 - "$cname" <<'PYEOF'
import json, subprocess, sys

name = sys.argv[1]
cfg = json.load(sys.stdin)[0]["Config"]

argv = ["docker", "create", "--name", name]
for e in cfg.get("Env") or []:
    # Inspect merges image-defined env (PATH etc.) into Config.Env; passing
    # those back through -e is harmless (same values the image sets anyway)
    # and guarantees the run-time-only vars (connection string, AGRI_DB_*)
    # are preserved exactly.
    argv += ["-e", e]
if cfg.get("Entrypoint"):
    argv += ["--entrypoint", cfg["Entrypoint"][0]]
argv += [cfg["Image"]]
if cfg.get("Entrypoint") and len(cfg["Entrypoint"]) > 1:
    argv += cfg["Entrypoint"][1:]
if cfg.get("Cmd"):
    argv += cfg["Cmd"]

subprocess.run(["docker", "rm", name], check=True, stdout=subprocess.DEVNULL)
# argv list, never a shell string: env values are passed verbatim and unprinted.
subprocess.run(argv, check=True, stdout=subprocess.DEVNULL)
print(f"  {name}: recreated (same name/env/command, new image build)")
PYEOF
}

build_api() {
  echo "[api] building agriforecast-api:latest (context: src/) ..."
  docker build -f "$SRC_DIR/AgriForecast.API/Dockerfile" -t agriforecast-api:latest "$SRC_DIR"
  echo "[api] image built."
  if $KUBECTL_OK; then
    kubectl rollout restart deployment/forecast-api -n "$NAMESPACE"
    echo "[api] deployment/forecast-api restarted — watch: kubectl rollout status deployment/forecast-api -n $NAMESPACE"
  else
    echo "[api] NOTE: cluster/namespace not reachable — image is built; restart later with:"
    echo "        kubectl rollout restart deployment/forecast-api -n $NAMESPACE"
  fi
}

build_ml() {
  echo "[ml] building agriforecast-ml:latest (context: src/AgriForecast.ML/) ..."
  docker build -t agriforecast-ml:latest "$SRC_DIR/AgriForecast.ML"
  echo "[ml] image built."
  if $KUBECTL_OK; then
    kubectl rollout restart deployment/ml-serving -n "$NAMESPACE"
    echo "[ml] deployment/ml-serving restarted — watch: kubectl rollout status deployment/ml-serving -n $NAMESPACE"
  else
    echo "[ml] NOTE: cluster/namespace not reachable — image is built; restart later with:"
    echo "        kubectl rollout restart deployment/ml-serving -n $NAMESPACE"
  fi
  echo "[ml] CronJobs (daily-pipeline, monthly-cbsl-macro) pick up the new image automatically on their next run."
  recreate_recovery_container agriforecast-daily-2-process agriforecast-ml:latest
}

build_ingestion() {
  echo "[ingestion] building agriforecast-ingestion:latest (context: src/) ..."
  docker build -f "$SRC_DIR/AgriForecast.Ingestion/Dockerfile" -t agriforecast-ingestion:latest "$SRC_DIR"
  echo "[ingestion] image built. No Deployment uses it — the daily-pipeline CronJob picks it up automatically on its next run."
  recreate_recovery_container agriforecast-daily-1-ingest agriforecast-ingestion:latest
}

case "$TARGET" in
  api) build_api ;;
  ml) build_ml ;;
  ingestion) build_ingestion ;;
  all)
    build_api
    echo ""
    build_ml
    echo ""
    build_ingestion
    ;;
esac

echo ""
echo "Done."
