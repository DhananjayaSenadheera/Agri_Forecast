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
| `secrets.template.yaml` | **Documentation only** — the shape of the secrets. Never fill it in |
| `create-secrets.sh` | Creates the real secrets from `src/AgriForecast.ML/.env` + a prompted JWT key (+ optional SMTP) |
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
It then offers the **optional** SMTP account for email alerts (see below);
press Enter to skip. Nothing is echoed; nothing secret is ever committed.
Re-run it after any credential rotation, then restart the deployments (the
script prints the command).

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

## Email alerts

The API contains a **pipeline sentinel**: once a night at **22:30 Asia/Colombo**
(90 minutes after the 21:00 fire) it reads its own
`GET /api/admin/pipeline/health` in-process and emails the owner when the night
was not green — `missing` (did not run), `failed`, `gate_blocked` or `partial`.
A night still `running` is re-read every 30 minutes until it settles, and so is
a `missing` one **until the 6-hour catch-up window closes at 03:00** — a node
asleep at 21:00 may legitimately start at 02:00 and still count, so an empty
window at 22:30 is "not yet", not "never". On a good
night it sends a one-line **all-clear heartbeat**, on purpose: an alert-only
sentinel is indistinguishable from a broken one, so missing mail is itself the
signal. Every message links back to `/admin/logs/ingestion`.

It is **opt-in and self-disabling**. With no `agri-smtp` secret the pod is
completely healthy; the API logs `sentinel disabled: Smtp not configured` once
at startup and never tries again.

**Turn it on (you run this — the repo holds no credential):**

1. Create a Gmail **app password** — Google requires one for SMTP and rejects
   the normal account password:
   [myaccount.google.com](https://myaccount.google.com) → **Security** →
   **2-Step Verification** (turn it on if it is not already) → **App
   passwords** → create one (any name, e.g. "AgriForecast") → copy the
   16-character value.
2. Run the secrets script and answer the three SMTP prompts (address, app
   password — hidden, recipient — defaults to the same address):
   ```bash
   ./k8s/create-secrets.sh
   ```
3. Restart the API so it picks the secret up (pods do **not** reload secrets):
   ```bash
   kubectl rollout restart deployment/forecast-api -n agriforecast
   kubectl logs deployment/forecast-api -n agriforecast | grep -i sentinel
   # expect: "Pipeline sentinel armed: nightly check at 22:30 Asia/Colombo; green heartbeat ON"
   ```

**Knobs** (`appsettings.json` / env, all optional):

| Key | Env | Default | Meaning |
|---|---|---|---|
| `Sentinel:LocalCheckTime` | `Sentinel__LocalCheckTime` | `22:30` | Check time, wall clock in `PipelineSchedule:TimeZone` |
| `Sentinel:SendGreenHeartbeat` | `Sentinel__SendGreenHeartbeat` | `true` | All-clear mail on a good night |
| `Sentinel:RunningRecheckMinutes` | `Sentinel__RunningRecheckMinutes` | `30` | Re-read interval for a still-running night |
| `Sentinel:AdminLogsUrl` | `Sentinel__AdminLogsUrl` | cluster UI ingestion log | Link at the foot of every message |
| `Smtp:Host` / `Smtp:Port` | `Smtp__Host` / `Smtp__Port` | `smtp.gmail.com` / `587` | STARTTLS |
| `Smtp:SendTimeoutSeconds` | `Smtp__SendTimeoutSeconds` | `30` | Hard deadline on one send (`SmtpClient.Timeout` does **not** bound `SendMailAsync`) |
| `Smtp:User` / `From` / `To` / `Password` | `Smtp__*` | *(empty)* | From `agri-smtp`; `From` defaults to `User`, `To` accepts a comma-separated list |

To turn alerts back **off**: `kubectl delete secret agri-smtp -n agriforecast`
and restart the deployment.

## The pipelines — supervised test, then cut over

Both CronJobs ship **suspended** on purpose: the launchd job that runs
`run-daily.sh` is still active, and running both would double-ingest. Do not
cut over until a supervised run has succeeded. (A late run backfills fine —
the DEC fetch returns full history per pass — but a failed run still costs a
day of freshness, so treat failures as urgent.)

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

### Pre-cutover checklist (both items gate the unsuspend, not the merge)

Two pieces of `run-daily.sh`'s operational scaffolding have **no pod
equivalent** and must be covered before the CronJob becomes the only daily
path:

1. **SQL Server readiness.** The script `docker start`s `sql_server_container`
   and polls `SELECT 1` for up to 120 s; a pod cannot start a host container,
   and the ingestion step fails immediately against a cold DB with no retry
   (`backoffLimit: 0`). Give the container a restart policy so it is always up
   when the machine is:

   ```bash
   docker update --restart unless-stopped sql_server_container
   ```

   `pipeline-daily.yaml` also now has a `wait-for-sql` init container (a
   bounded ~5 min TCP poll of `host.docker.internal:$AGRI_DB_PORT`, read from
   the same `agri-db` secret every other step uses, before `batch-id`) as
   defense-in-depth for the "up but not ready yet" window. It does NOT
   replace the restart policy above — that's still the fix for the DB not
   being up at all; the init container just makes a cold-start failure
   surface as a clean, visible Job failure instead of the ingestion step
   dying immediately with no retry.

2. **Failure visibility.** The script's EXIT trap prints a FAILED banner and
   fires a macOS notification — the control added after the pipeline failed
   silently for 8 straight mornings. A failed CronJob run is only visible via
   `kubectl`, so until proper alerting exists, check daily (or wire this into
   a login shell / a small launchd check):

   ```bash
   kubectl get jobs -n agriforecast --sort-by=.metadata.creationTimestamp
   # any job with COMPLETIONS 0/1 = a failed pipeline run — inspect its step logs
   ```

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

## Resource sizing + security hardening (PR-3, the PR #62 declared follow-up)

No metrics-server is installed on this cluster (`kubectl top pods/nodes` ->
"Metrics API not available"), so `requests`/`limits` on every container are
conservative, hand-picked defaults, cross-checked against `docker stats` on
the live pods where a baseline existed at write time (idle):

| Container(s) | Observed idle | requests | limits | Why |
|---|---|---|---|---|
| forecast-api | ~41MiB | 256Mi / 100m | 1Gi / 500m | ASP.NET Core, EF Core; ~6x headroom over idle for real request load |
| forecast-ui | ~9MiB | *(ForecastUI repo — separate PR)* | | nginx serving a static SPA; lightest of the three Deployments |
| ml-serving | ~307MiB idle (model loaded) | 512Mi / 250m | **3Gi** / 1000m | Sized for PARSE PEAKS, not idle: this pod also runs the pdfplumber CBSL/HARTI parse routes nightly, and an OOMKill here takes down forecasts + three ingestion sources at once. Reviewer measured `build-features` (a lighter DB-I/O step) at 568MB peak during this PR's verification — real headroom for pdfplumber's heavier per-page cost is 3Gi, not a limit sized off an idle FastAPI process |
| pipeline: trivial shell steps (wait-for-sql, batch-id) | n/a | 16-32Mi / 10m | 32-64Mi / 50-100m | Fixed tiny footprint |
| pipeline: light DB-I/O python/dotnet steps (ingestion, mirror-dec, verify, ingest-news, score-news) | n/a | 128Mi / 100m | 512Mi / 500m | No heavy libraries loaded (no torch/transformers — VADER is rule-based) |
| pipeline: build-features (now includes the qa_features gate in-process) | measured 568MB peak | 512Mi / 250m | 1536Mi / 1000m | Loads full price/weather/fx/policy/sentiment history via pandas, persists CropFeatureDaily, then runs qa_features.run_qa() in the same process (re-reads the full table + rebuilds Beans' 11-year history twice more for TC5) before marking the run row Succeeded |
| monthly: ingest-cbsl-macro | n/a | 256Mi / 100m | 768Mi / 500m | pdfplumber PDF parsing is more memory-hungry per page than plain DB I/O |

**forecast-ui.yaml lives in the separate ForecastUI repo** (this is the
backend repo) — its resources/securityContext are a companion PR there, not
included here.

**securityContext — verified per image, not asserted:**
- `agriforecast-api` and `agriforecast-ingestion` (.NET) both run as the
  aspnet/runtime base image's built-in non-root `app` user (uid 1654 via
  `$APP_UID`) — `agriforecast-ingestion`'s Dockerfile was missing the `USER
  $APP_UID` line the API image already had; added and verified (`docker run
  --rm --entrypoint id agriforecast-ingestion:latest` -> `uid=0(root)` before,
  `uid=1654(app)` after). Both get `runAsNonRoot: true`.
- `agriforecast-ml` (python:3.12-slim) has **no non-root user baked in** —
  verified empirically (`docker run --rm --entrypoint id
  agriforecast-ml:latest` -> `uid=0(root)`). `runAsNonRoot` is honestly left
  **unset** for every container using this image (ml-serving + all the
  python pipeline steps) rather than forced, which would crash-loop them.
  They still get `allowPrivilegeEscalation: false` + `capabilities: {drop:
  [ALL]}`, which don't require non-root. Making this image non-root is a
  separate follow-up: add a user in the Dockerfile, `chown /app`, re-verify
  pip/xgboost/shap still import and the readOnly `/app/models` mount still
  works.

## Cloud-portability notes (for the later phases)

- `host.docker.internal` → becomes a managed-DB hostname in the connection
  secret; every manifest already reads host/port from env.
- The ML `models/` hostPath → becomes a PVC or object storage (S3/Azure Blob)
  populated by the training job; only the volume block changes.
- `imagePullPolicy: IfNotPresent` + `:latest` local tags → becomes a registry
  with immutable tags.
- NodePort 30082 → becomes an Ingress/LoadBalancer with TLS.
