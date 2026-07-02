#!/bin/sh
# Starts the AgriForecast ML service locally.
# Secrets live in .env (gitignored) — this script stays secret-free.
cd "$(dirname "$0")" || exit 1
if [ -f .env ]; then
  set -a; . ./.env; set +a
fi
exec .venv/bin/uvicorn agriforecast_ml.serving.app:app --port 8077 "$@"
