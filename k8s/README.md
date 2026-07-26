# AgriForecast on Kubernetes (Docker Desktop) — Runbook

Phase 1–2 of the migration plan: the API, ML serving, and the daily/monthly
data pipelines run inside Docker Desktop's single-node Kubernetes cluster.
SQL Server **stays outside** the cluster (host container
`sql_server_container`, port 1434) — pods reach it via `host.docker.internal`.
No RabbitMQ yet (later phase). No application code was changed for this.

## What's in this directory

| File | Purpose |
|---|---|
| `namespace.yaml` | Namespace `agriforecast` — everything lives here |
| `secrets.template.yaml` | **Documentation only** — the shape of the 3 secrets. Never fill it in |
| `create-secrets.sh` | Creates the real secrets from `src/AgriForecast.ML/.env` + a prompted JWT key |
| `ml-serving.yaml` | ML FastAPI Deployment + **ClusterIP** Service `ml-serving:8077` (internal-only on purpose) |
| `forecast-api.yaml` | .NET API Deployment + **NodePort** Service → `http://localhost:30082` |
| `pipeline-daily.yaml` | CronJob `daily-pipeline`, 21:00 Asia/Colombo, **suspended by default** |
| `pipeline-monthly.yaml` | CronJob `monthly-cbsl-macro`, 1st of month 21:00 Colombo, **suspended by default** |
| `redeploy.sh` | Rebuild image(s) + roll them into the cluster after a code change |

## First-time setup

### 1. Enable Kubernetes in Docker Desktop

Docker Desktop → Settings → **Kubernetes** → *Enable Kubernetes* → Apply &
Restart. Wait for the green "Kubernetes running" indicator, then check:

```bash
kubectl config use-context docker-desktop
kubectl get nodes    # one node, STATUS Ready
```

### 2. Build the images

```bash
cd /Users/dhananjayasenadheera/Projects/Agri_Forecast/Project/Agri_Forecast
docker build -f src/AgriForecast.API/Dockerfile       -t agriforecast-api:latest       src/
docker build -f src/AgriForecast.Ingestion/Dockerfile -t agriforecast-ingestion:latest src/
docker build -t agriforecast-ml:latest src/AgriForecast.ML/
```

(Or, once everything below is applied: `./k8s/redeploy.sh all`.)

The cluster uses these local images directly (`imagePullPolicy: IfNotPresent`,
no registry) — Docker Desktop's Kubernetes shares the host image store.

### 3. Create the secrets

```bash
./k8s/create-secrets.sh
```

It reads DB creds + the ML admin key from the gitignored
`src/AgriForecast.ML/.env` and prompts (hidden input) for the JWT signing key
— copy that from `dotnet user-secrets list --project src/AgriForecast.API`.
Nothing is echoed; nothing secret is ever committed. Re-run it after any
credential rotation, then restart the deployments (the script prints the
command).

### 4. Apply the manifests

```bash
kubectl apply -f k8s/namespace.yaml
kubectl apply -f k8s/ml-serving.yaml
kubectl apply -f k8s/forecast-api.yaml
kubectl apply -f k8s/pipeline-daily.yaml
kubectl apply -f k8s/pipeline-monthly.yaml
```

(Deliberately listed file-by-file rather than `-f k8s/` so
`secrets.template.yaml`'s placeholders can never clobber real secrets. If
that ever happens anyway, re-run `./k8s/create-secrets.sh`.)

### 5. Verify

```bash
kubectl get pods -n agriforecast          # ml-serving + forecast-api Running, READY 1/1
kubectl get cronjobs -n agriforecast      # both listed, SUSPEND True
curl http://localhost:30082/health        # {"status":"healthy"}
```

The ML service is intentionally **not** reachable from the host (its predict
routes are unauthenticated; being cluster-internal IS the security boundary).
To spot-check it: `kubectl port-forward svc/ml-serving 8077:8077 -n
agriforecast` and curl `http://localhost:8077/health`, then Ctrl-C.

## The pipelines — supervised test, then cut over

Both CronJobs ship **suspended** on purpose: the launchd job that runs
`run-daily.sh` is still active, and running both would double-ingest. Also, a
failed/missed daily run **permanently loses that day's DEC data** (the scraper
only ever gets the current day), so do not cut over until a supervised run has
succeeded.

### Supervised run (do this once, watching)

```bash
kubectl create job --from=cronjob/daily-pipeline daily-test -n agriforecast
kubectl get pods -n agriforecast -w      # watch the init chain: batch-id → ingestion → mirror-dec → verify → ingest-news → score-news → build-features
```

Logs per step (init containers need `-c`):

```bash
kubectl logs -n agriforecast -l job-name=daily-test -c ingestion
kubectl logs -n agriforecast -l job-name=daily-test -c verify
kubectl logs -n agriforecast -l job-name=daily-test -c build-features
```

The `verify` step is the gate: if the 14-check battery FAILs, the pod stops
there and nothing after it runs (`backoffLimit: 0` — no blind retries).
When satisfied: `kubectl delete job daily-test -n agriforecast`.

Note: a manual test run ingests real data for **today** — that is fine (the
ingestion is idempotent per day), but do it instead of, not in addition to,
letting launchd also run that afternoon if you can.

### Cut over (only after the supervised run succeeded)

```bash
# 1. Unsuspend the CronJobs
kubectl patch cronjob daily-pipeline      -n agriforecast -p '{"spec":{"suspend":false}}'
kubectl patch cronjob monthly-cbsl-macro  -n agriforecast -p '{"spec":{"suspend":false}}'

# 2. Disable the launchd daily job so it can't double-run
launchctl bootout gui/$(id -u)/com.agriforecast.daily
# (label per `launchctl list | grep agriforecast`; if it differs, bootout that one.
#  To re-enable later: launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.agriforecast.daily.plist)
```

The monthly CronJob replaces the local `run-monthly.sh` — which hardcodes the
DB password; retire that script and **rotate that password** once the CronJob
is live.

## Updating after a code change

```bash
./k8s/redeploy.sh api        # or ml | ingestion | all
```

It rebuilds the right image with the right context, restarts the matching
Deployment (CronJobs pick the new image up on their next run automatically),
and recreates the two Docker-Desktop recovery containers
(`agriforecast-daily-1-ingest`, `agriforecast-daily-2-process`) so they don't
keep pointing at a stale image build.

## Dual-mode note

The cluster API on **:30082** runs in parallel with the host-run dev API on
**:5282** (`dotnet run`) — nothing about local development changes. Both talk
to the same SQL Server. The dev frontend (vite, :5173/:4173) is in the API's
CORS allowlist for both instances.

## Rollback

```bash
kubectl delete -f k8s/pipeline-daily.yaml -f k8s/pipeline-monthly.yaml \
               -f k8s/forecast-api.yaml   -f k8s/ml-serving.yaml
# then re-enable launchd if you disabled it:
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.agriforecast.daily.plist
```

`kubectl delete namespace agriforecast` removes everything including the
secrets. The database, the images, and the local scripts are untouched —
`run-daily.sh` + launchd keep working exactly as before.

## Cloud-portability notes (for the later phases)

- `host.docker.internal` → becomes a managed-DB hostname in the connection
  secret; every manifest already reads host/port from env.
- The ML `models/` hostPath → becomes a PVC or object storage (S3/Azure Blob)
  populated by the training job; only the volume block changes.
- `imagePullPolicy: IfNotPresent` + `:latest` local tags → becomes a registry
  with immutable tags.
- NodePort 30082 → becomes an Ingress/LoadBalancer with TLS.
