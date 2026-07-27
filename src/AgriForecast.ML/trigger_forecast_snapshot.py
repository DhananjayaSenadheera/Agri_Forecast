"""Nightly forecast-snapshot trigger: entry point invoked by k8s/pipeline-daily.yaml
AFTER build_features.py finishes, in the SAME container (the build-features main
container of the daily CronJob).

CORRECTED ARCHITECTURE (PRD farmer-portfolio-and-forecast-snapshots.md, 4.2, note
dated 2026-07-27): the snapshot pass reads CropFeatureDaily, which is only rebuilt at
the build_features pipeline step. A trigger placed earlier (e.g. in the .NET Worker
pass, as originally spec'd) would predict and score against a feature store one
pipeline-day stale. This script is therefore invoked from the pipeline manifest's
build-features container, after build_features.py has already exited 0 - never
before, and never when build_features failed (see the container's shell wrapper in
pipeline-daily.yaml; a failed build simply skips this script that night, and pending
rows catch up the next night by design).

What this script does, in order:
  1. FORECAST_SNAPSHOTS_ENABLED (default "true"): if falsy, record ONE Skipped
     IngestionRuns row (Source='FORECAST_SNAPSHOT') and exit 0. Never silent.
  2. POST an EMPTY-of-snapshotDate body to <ML_SERVICE_BASE_URL>/admin/snapshot-forecasts
     with the X-API-Key admin header (ML_ADMIN_API_KEY - same secret/env pattern the
     .NET ingestion Worker already uses for HARTI/CBSL/NEWS: MlService:BaseUrl /
     MlService:AdminApiKey, mirrored here as plain env vars since this runs as a
     standalone Python step, not through ASP.NET config binding).

     The body is deliberately EMPTY of snapshotDate: the service defaults to
     date.today() on its own clock. A watermark-derived or otherwise computed date
     would, after any outage longer than SNAPSHOT_MATURE_GRACE_DAYS (7), be rejected
     by the service's own validator FOREVER (see snapshots._validate_snapshot_date)
     AND stop the pending-row maturing sweep from ever running again - the service is
     the one place that date logic belongs, this caller must not re-derive it.
  3. Records exactly ONE IngestionRuns row, Source='FORECAST_SNAPSHOT':
       - Succeeded: HTTP 200, response status=="ok", AND
         errors.snapshotCropFailures == 0 AND errors.matureRowFailures == 0.
       - Failed (with a human-readable summary, counts included): a transport error,
         a non-200 response, a 422 (recorded distinctly - a caller bug, not a pass
         failure, since we never send a date that could be rejected), OR a 200 "ok"
         response that nonetheless reports snapshotCropFailures>0 or
         matureRowFailures>0. THIS LAST CASE IS THE POINT: a crop failing every
         single night must show up as a visibly non-green run in the admin Logs hub,
         not be swept under an "ok" HTTP status.
  4. FAIL-SOFT (PRD 3.7): this script NEVER raises out of main() and ALWAYS exits 0.
     A snapshot-pass failure must never fail the pipeline Job; the container's own
     exit code is build_features.py's, not this script's (enforced by the `|| true`
     in the manifest's shell wrapper, and independently here by construction).
"""
from __future__ import annotations

import json
import logging
import os
import sys
import urllib.error
import urllib.request
from datetime import date
from typing import Any, Callable, Optional

from agriforecast_ml import snapshot_run_log
from agriforecast_ml.db import get_engine
from agriforecast_ml.envfile import load_env_file

logger = logging.getLogger(__name__)

_ENABLED_ENV = "FORECAST_SNAPSHOTS_ENABLED"
_BASE_URL_ENV = "ML_SERVICE_BASE_URL"
_API_KEY_ENV = "ML_ADMIN_API_KEY"
_DEFAULT_BASE_URL = "http://127.0.0.1:8077"
_ENDPOINT_PATH = "/admin/snapshot-forecasts"
_TIMEOUT_SECONDS = 120

_FALSY = {"false", "0", "no", "off", ""}

HttpPost = Callable[[str, str], "tuple[int, Optional[dict], str]"]


def _is_enabled() -> bool:
    """FORECAST_SNAPSHOTS_ENABLED, default "true" -- absent/unset means enabled, and
    ONLY these tokens (case-insensitive) count as off, so a typo in a manifest value
    fails safe towards "still runs", never towards silently going dark."""
    return os.getenv(_ENABLED_ENV, "true").strip().lower() not in _FALSY


def _safe_json(raw: str) -> Optional[dict]:
    try:
        parsed = json.loads(raw)
    except (json.JSONDecodeError, TypeError):
        return None
    return parsed if isinstance(parsed, dict) else None


def _call_snapshot_endpoint(base_url: str, api_key: str) -> "tuple[int, Optional[dict], str]":
    """POST an empty JSON object (no snapshotDate -- see module docstring) to
    /admin/snapshot-forecasts. Returns (status_code, parsed_json_or_None, raw_body).

    Raises on a transport failure (DNS/connection/timeout) -- the caller records that
    distinctly from an HTTP error status. Any HTTP status code, including 4xx/5xx, is
    returned normally (via urllib.error.HTTPError, caught here) rather than raised,
    so the caller can tell "we could not reach ml-serving" apart from "ml-serving
    answered but said no".
    """
    url = base_url.rstrip("/") + _ENDPOINT_PATH
    req = urllib.request.Request(
        url,
        data=b"{}",
        method="POST",
        headers={"Content-Type": "application/json", "X-API-Key": api_key},
    )
    try:
        with urllib.request.urlopen(req, timeout=_TIMEOUT_SECONDS) as resp:
            raw = resp.read().decode("utf-8", errors="replace")
            return resp.status, _safe_json(raw), raw
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", errors="replace") if exc.fp else ""
        return exc.code, _safe_json(raw), raw


def _summarize(payload: dict) -> str:
    """Human-readable one-liner with every count an admin needs to see, including
    `frozen` (PRD 4.2's `snapshot.frozen`) -- without it a re-run over an
    already-matured day looks like it silently did nothing."""
    snap = payload.get("snapshot") or {}
    mature = payload.get("mature") or {}
    errors = payload.get("errors") or {}
    return (
        f"snapshotDate={snap.get('snapshotDate')} cropsAttempted={snap.get('cropsAttempted')} "
        f"inserted={snap.get('inserted')} updated={snap.get('updated')} frozen={snap.get('frozen')} "
        f"modelServed={snap.get('modelServed')} fallbackServed={snap.get('fallbackServed')} "
        f"notMaturable={snap.get('notMaturable')} modelVersion={snap.get('modelVersion')} | "
        f"mature.scanned={mature.get('scanned')} matured={mature.get('matured')} "
        f"stillPending={mature.get('stillPending')} markedUnavailable={mature.get('markedUnavailable')} | "
        f"snapshotCropFailures={errors.get('snapshotCropFailures')} "
        f"matureRowFailures={errors.get('matureRowFailures')}"
    )


def _try_start(engine) -> Optional[Any]:
    try:
        run_id, _batch_id, _started = snapshot_run_log.start_run(engine)
        return run_id
    except Exception as exc:  # noqa: BLE001 -- audit bookkeeping must never block the trigger
        logger.warning(
            "trigger_forecast_snapshot: Running row not recorded (%s: %s); continuing unaudited.",
            type(exc).__name__, exc)
        return None


def _try_finish_succeeded(engine, run_id, payload: dict) -> None:
    if run_id is None:
        return
    try:
        snap = payload.get("snapshot") or {}
        mature = payload.get("mature") or {}
        snap_date_str = snap.get("snapshotDate")
        covered = date.fromisoformat(snap_date_str) if snap_date_str else None
        snapshot_run_log.mark_succeeded(
            engine, run_id,
            rows_inserted=int(snap.get("inserted") or 0) + int(snap.get("updated") or 0),
            rows_skipped=snap.get("frozen"),
            rows_fetched=mature.get("scanned"),
            distinct_crops=snap.get("cropsAttempted"),
            covered_from=covered,
            covered_to=covered,
        )
    except Exception as exc:  # noqa: BLE001 -- audit bookkeeping must never block the trigger
        logger.warning(
            "trigger_forecast_snapshot: Succeeded row not recorded (%s: %s).",
            type(exc).__name__, exc)


def _try_finish_failed(engine, run_id, summary: str) -> None:
    if run_id is None:
        return
    try:
        snapshot_run_log.mark_failed(engine, run_id, summary)
    except Exception as exc:  # noqa: BLE001 -- audit bookkeeping must never block the trigger
        logger.warning(
            "trigger_forecast_snapshot: Failed row not recorded (%s: %s).",
            type(exc).__name__, exc)


def _try_finish_skipped(engine, run_id) -> None:
    if run_id is None:
        return
    try:
        snapshot_run_log.mark_skipped(engine, run_id)
    except Exception as exc:  # noqa: BLE001 -- audit bookkeeping must never block the trigger
        logger.warning(
            "trigger_forecast_snapshot: Skipped row not recorded (%s: %s).",
            type(exc).__name__, exc)


def skip(engine) -> str:
    """The FORECAST_SNAPSHOTS_ENABLED=false path: no HTTP call is made at all, a
    Skipped row is recorded (never silent, never a failure). Returns "skipped"."""
    run_id = _try_start(engine)
    _try_finish_skipped(engine, run_id)
    return "skipped"


def run(engine, base_url: str, api_key: str, *, http_post: HttpPost = _call_snapshot_endpoint) -> str:
    """Core trigger logic: call the admin endpoint once, decide the outcome, record
    ONE audit row. Never raises -- every branch below is terminal and returns a
    status string ("succeeded" | "failed"); the DB engine and the HTTP caller are
    both injectable so this is testable with no live DB and no live network.
    """
    run_id = _try_start(engine)

    try:
        status, payload, raw = http_post(base_url, api_key)
    except Exception as exc:  # noqa: BLE001 -- a transport failure, not a pass failure
        logger.exception("trigger_forecast_snapshot: transport failure calling %s", _ENDPOINT_PATH)
        _try_finish_failed(
            engine, run_id,
            f"Transport failure calling {_ENDPOINT_PATH}: {type(exc).__name__}: {exc}")
        return "failed"

    if status == 422:
        detail = (payload or {}).get("detail", raw) if isinstance(payload, dict) else raw
        reason = f"422 CALLER-BUG (not a pass failure): ML rejected the request: {str(detail)[:500]}"
        logger.error("trigger_forecast_snapshot: %s", reason)
        _try_finish_failed(engine, run_id, reason)
        return "failed"

    if status != 200 or payload is None:
        reason = f"ML returned HTTP {status}. Body: {raw[:500]}"
        logger.error("trigger_forecast_snapshot: %s", reason)
        _try_finish_failed(engine, run_id, reason)
        return "failed"

    errors = payload.get("errors") or {}
    crop_failures = errors.get("snapshotCropFailures") or 0
    row_failures = errors.get("matureRowFailures") or 0
    summary = _summarize(payload)

    if payload.get("status") == "ok" and crop_failures == 0 and row_failures == 0:
        logger.info("trigger_forecast_snapshot: Succeeded -- %s", summary)
        _try_finish_succeeded(engine, run_id, payload)
        return "succeeded"

    # THE 200-"ok"-with-failures trap: an HTTP-successful, status="ok" response can
    # still carry per-crop/per-row failures. Recording that as Succeeded would hide a
    # crop failing every night behind a green run row -- record Failed instead, with
    # the counts (including `frozen`) in the summary.
    reason = f"Pass reported per-crop/per-row failures: {summary}"
    logger.error("trigger_forecast_snapshot: %s", reason)
    _try_finish_failed(engine, run_id, reason)
    return "failed"


def main() -> int:
    load_env_file()
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)-8s %(name)s -- %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    if not _is_enabled():
        logger.info(
            "trigger_forecast_snapshot: DISABLED (%s=%r) -- skipping this run, recording a Skipped row.",
            _ENABLED_ENV, os.getenv(_ENABLED_ENV))
        try:
            engine = get_engine()
        except Exception as exc:  # noqa: BLE001 -- never fail the pipeline over audit bookkeeping
            logger.warning(
                "trigger_forecast_snapshot: no DB engine available to record the Skipped row (%s: %s).",
                type(exc).__name__, exc)
            return 0
        skip(engine)
        return 0

    base_url = os.getenv(_BASE_URL_ENV, _DEFAULT_BASE_URL)
    api_key = os.getenv(_API_KEY_ENV, "")
    if not api_key:
        logger.warning(
            "trigger_forecast_snapshot: %s is not set -- the admin endpoint will answer 401/403; "
            "proceeding anyway so the failure is recorded honestly rather than special-cased away.",
            _API_KEY_ENV)

    try:
        engine = get_engine()
    except Exception as exc:  # noqa: BLE001 -- never fail the pipeline over audit bookkeeping
        logger.error(
            "trigger_forecast_snapshot: no DB engine available (%s: %s) -- cannot record an audit "
            "row locally. Still attempting the HTTP call so the pass can run server-side even "
            "though this run stays unaudited.",
            type(exc).__name__, exc)
        try:
            _call_snapshot_endpoint(base_url, api_key)
        except Exception:  # noqa: BLE001 -- best-effort only, nothing left to record it against
            logger.exception("trigger_forecast_snapshot: unaudited attempt also failed transport.")
        return 0

    outcome = run(engine, base_url, api_key)
    logger.info("trigger_forecast_snapshot: finished with outcome=%s", outcome)
    return 0


if __name__ == "__main__":
    sys.exit(main())
