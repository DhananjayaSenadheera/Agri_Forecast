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

# Recreate a pre-created recovery container in place, preserving its name,
# its runtime-only env vars (connection string / AGRI_DB_*), and its
# entrypoint/cmd. NOT preserved: HostConfig (binds, network mode, restart
# policy, port mappings) — both recovery containers were verified to use
# defaults for all of those; the script warns and skips if that ever stops
# being true rather than silently dropping settings. Uses python3 so config
# travels as JSON -> argv list; no secret env value is ever echoed or
# shell-interpolated. (Deliberately NOT `docker inspect | python3 - <<EOF`:
# the heredoc would replace the pipe as stdin and the interpreter would eat
# it — python fetches the JSON itself.)
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
  python3 - "$cname" <<'PYEOF'
import json, subprocess, sys

name = sys.argv[1]


def inspect(kind, ref):
    out = subprocess.run(["docker", *kind, "inspect", ref],
                         check=True, capture_output=True, text=True).stdout
    return json.loads(out)[0]


info = inspect([], name)
cfg = info["Config"]
host = info.get("HostConfig") or {}

# Refuse to recreate (rather than silently drop settings) if the container
# carries non-default HostConfig we don't reproduce.
nondefault = []
if host.get("Binds"):
    nondefault.append("Binds")
if info.get("Mounts"):
    nondefault.append("Mounts")
if host.get("PortBindings"):
    nondefault.append("PortBindings")
if (host.get("RestartPolicy") or {}).get("Name") not in (None, "", "no"):
    nondefault.append("RestartPolicy")
if host.get("NetworkMode") not in (None, "", "default", "bridge"):
    nondefault.append("NetworkMode")
if nondefault:
    print(f"  WARNING: {name} uses non-default HostConfig ({', '.join(nondefault)}) "
          f"this script does not reproduce — left as-is. Recreate it by hand "
          f"against the fresh image.")
    sys.exit(0)

# Only re-pass env vars the image does NOT define itself: re-passing the
# image's own PATH/PYTHON_*/DOTNET_* would pin the OLD image's values onto
# the new build. A var whose NAME the fresh image defines but with a
# DIFFERENT value is ambiguous (stale old-image value vs. deliberate
# override) — dropped with a named warning, so nothing is pinned silently.
image_vars = dict(
    e.split("=", 1)
    for e in inspect(["image"], cfg["Image"]).get("Config", {}).get("Env") or []
)
runtime_env = []
for e in cfg.get("Env") or []:
    k, _, v = e.partition("=")
    if k not in image_vars:
        runtime_env.append(e)  # true runtime-only var (conn string, AGRI_DB_*)
    elif v != image_vars[k]:
        print(f"  note: {k} differed from the image default — not carried over "
              f"(re-set it manually on the container if the override was intentional).")

argv = ["docker", "create", "--name", name]
for e in runtime_env:
    argv += ["-e", e]
if cfg.get("Entrypoint"):
    argv += ["--entrypoint", cfg["Entrypoint"][0]]
argv += [cfg["Image"]]
if cfg.get("Entrypoint") and len(cfg["Entrypoint"]) > 1:
    argv += cfg["Entrypoint"][1:]
if cfg.get("Cmd"):
    argv += cfg["Cmd"]

subprocess.run(["docker", "rm", "-f", name], check=True, stdout=subprocess.DEVNULL)
# argv list, never a shell string: env values are passed verbatim and unprinted.
subprocess.run(argv, check=True, stdout=subprocess.DEVNULL)
print(f"  {name}: recreated ({len(runtime_env)} runtime env var(s) carried over, new image build)")
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
