# AgriForecast ML microservice (FastAPI — Model A harvest-price predictor).
#
# Build context: src/AgriForecast.ML/
#   docker build -t agriforecast-ml .
#
# RUNTIME REQUIREMENT — the model registry is NOT baked into this image.
#   The file registry (models/<version>/model.pkl + metadata.json + promoted.json)
#   is produced by train_model_a.py, which needs the SQL Server DB. It is
#   gitignored and intentionally NOT trained at build time. At runtime the
#   container MUST be given a populated models/ directory, e.g.:
#       docker run -v $(pwd)/models:/app/models ... agriforecast-ml
#   or have it baked in via a separate init/training step. Without it, startup
#   (registry.load_promoted at import in serving/predict.py) will fail.
#
# DB credentials come from env vars (AGRI_DB_*) or a mounted appsettings.json —
# never hard-coded here. No secrets in this Dockerfile.
FROM python:3.12-slim

# libgomp1: OpenMP runtime required by xgboost at inference time.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

ENV PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1

# Install deps first for layer caching.
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Application code: the package + the entry-point scripts.
# ingest_news.py / score_news.py are imported by the /admin/ingest-news
# endpoint (serving/app.py), so they must be present in the image.
COPY agriforecast_ml ./agriforecast_ml
COPY build_features.py .
COPY train_model_a.py .
COPY ingest_news.py .
COPY score_news.py .
# ingest_cbsl_macro.py is invoked by run-monthly.sh against this image.
COPY ingest_cbsl_macro.py .
# ingest_harti_service.py is lazily imported by the /admin/ingest-harti
# endpoint (serving/app.py:256). SF3 (R2 Step 2 reviewer should-fix): this was
# a PRE-EXISTING latent gap -- the script was never COPY'd here, so that
# endpoint 500s in a freshly built image (same class of bug as the
# ingest_cbsl_macro.py incident). Fixed here since this Dockerfile is already
# being touched in this change; unrelated to the DEC mirror itself.
COPY ingest_harti_service.py .
# mirror_dec_prices.py (R2 Step 2, DEC -> PriceObservations mirror) is invoked
# by run-daily.sh, one step after market ingest / before build_features. A
# missed COPY here is exactly the ingest_cbsl_macro.py class of bug (script
# absent from a freshly-built image) -- see run-daily.sh's daily wiring.
COPY mirror_dec_prices.py .
# verify_ingestion.py (post-ingestion 14-check verification battery) is
# invoked by run-daily.sh AFTER mirror_dec_prices.py / BEFORE build_features.
# Same class of bug as above if omitted -- see agriforecast_ml/ingest_verify.py.
COPY verify_ingestion.py .
# qa_features.py (feature-store QA harness) is `import qa_features`'d
# directly by build_features.py (its post-persist QA gate -- see
# build_features.py's `_run_qa_gate`), not invoked as a separate script.
# Still needs its own COPY: same class of bug as above if omitted -- caught
# here BEFORE it could ship, by dry-testing the actual image build+run in
# the nightly-qa-gate PR rather than assuming the COPY list was already
# complete.
COPY qa_features.py .
# trigger_forecast_snapshot.py (PR 0c, farmer-portfolio PRD 4.2) is invoked by
# k8s/pipeline-daily.yaml's build-features container shell wrapper, AFTER
# build_features.py exits 0 -- same class of bug as every entry above if
# omitted: the interpreter would exit 2 (script not found), silently
# swallowed by the manifest's `|| true` fail-soft wrapper, and zero
# FORECAST_SNAPSHOT audit rows would ever be written. Caught here BEFORE it
# could ship, same discipline as the qa_features.py incident above: this
# Dockerfile change is proved with a real `docker build` + `docker run`
# below, not assumed correct from the COPY list alone.
COPY trigger_forecast_snapshot.py .

EXPOSE 8077

CMD ["uvicorn", "agriforecast_ml.serving.app:app", "--host", "0.0.0.0", "--port", "8077"]
