"""FastAPI service exposing harvest-price predictions.

Internal service called by the .NET API. /health, /model-info, /predict and /timeline
are unauthenticated by design; every /admin/* route requires an X-API-Key header
matching the ML_ADMIN_API_KEY environment variable.
"""
from __future__ import annotations

import hmac
import logging
import os
from datetime import date
from typing import Optional

from fastapi import APIRouter, Depends, FastAPI, Header, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from ..envfile import load_env_file

# Load the gitignored .env before anything reads the environment, so the service works
# however uvicorn was launched. Real environment variables still take precedence.
load_env_file()

from . import predict  # noqa: E402  (env must be loaded first)

app = FastAPI(title="AgriForecast ML — Model A", version="1.0")

_log = logging.getLogger(__name__)

_ADMIN_API_KEY_ENV = "ML_ADMIN_API_KEY"


@app.exception_handler(Exception)
async def unhandled_exception_handler(request: Request, exc: Exception) -> JSONResponse:
    """Global backstop for any unhandled exception in any route.

    Routes already catch their own errors; this catches whatever they miss, so a debug
    configuration can never leak a traceback. The full exception is logged server-side
    only - never request bodies or headers, which can carry the admin X-API-Key - and the
    client always gets a fixed generic 500.

    HTTPException is handled earlier in the stack, so 401/502/503 raised in routes keep
    their intended status and detail.
    """
    _log.exception(
        "Unhandled exception serving %s %s", request.method, request.url.path
    )
    return JSONResponse(status_code=500, content={"detail": "Internal server error."})


def require_api_key(x_api_key: Optional[str] = Header(default=None)) -> None:
    """Auth dependency for /admin/* routes.

    Compares the caller's X-API-Key header to the ML_ADMIN_API_KEY env var in constant
    time. An unset or empty server key returns 500, never allow-all; a missing or
    mismatched header returns 401.
    """
    expected = os.getenv(_ADMIN_API_KEY_ENV, "")
    if not expected:
        _log.error(
            "%s is unset/empty; refusing admin request (fail-closed).",
            _ADMIN_API_KEY_ENV,
        )
        raise HTTPException(
            status_code=500,
            detail="Server auth misconfiguration: admin API key not configured.",
        )
    if x_api_key is None or not hmac.compare_digest(x_api_key, expected):
        raise HTTPException(status_code=401, detail="Invalid or missing API key.")


# All /admin/* routes inherit require_api_key, so a future admin route is protected too.
admin_router = APIRouter(prefix="/admin", dependencies=[Depends(require_api_key)])


class PredictRequest(BaseModel):
    cropId: str
    plantDate: date


class TimelineRequest(BaseModel):
    cropId: str
    asOf: Optional[date] = None
    months: int = Field(default=12, ge=1, le=24)


class HarvestWindowRequest(BaseModel):
    cropId: str
    asOf: Optional[date] = None
    # Upper bound is one seasonal cycle: past that the frozen price/weather anchor is too
    # stale for the comparison to mean anything.
    horizonDays: int = Field(default=90, ge=7, le=365)


@app.get("/health")
def health():
    return {"status": "ok"}


@app.get("/model-info")
def model_info():
    return predict.model_info()


@app.get("/crop-readiness")
def crop_readiness_endpoint():
    """Per-crop readiness map for the app's crop-status colouring.

    Any unexpected failure returns the honest empty shape (modelActive=false), never a 500.
    """
    try:
        return predict.crop_readiness()
    except Exception:
        _log.exception("crop_readiness failed — returning empty readiness map")
        return {"modelVersion": None, "minHistoryObs": None, "modelActive": False, "crops": {}}


@app.post("/predict")
def predict_endpoint(req: PredictRequest):
    return predict.predict_harvest(req.cropId, req.plantDate)


@app.post("/harvest-window")
def harvest_window_endpoint(req: HarvestWindowRequest):
    """Best planting window for one crop, ranking candidate planting dates.

    The honest answer to 'we could not work this out' is rankable=false with a reason,
    never a 500 and never a fabricated window.
    """
    as_of = req.asOf or date.today()
    try:
        return predict.harvest_window(req.cropId, as_of, req.horizonDays)
    except Exception:
        _log.exception("harvest_window failed — returning the not-rankable shape")
        return {
            "cropId": str(req.cropId).lower(),
            "cropName": None,
            "asOf": as_of.isoformat(),
            "growthPeriodDays": None,
            "rankable": False,
            "reasonCode": "unavailable",
            "activePredictor": "unavailable",
            "confidence": "Low",
            "modelVersion": None,
            "explanation": "We could not compare planting dates for this crop just now.",
            "windowDays": None,
            "points": [],
            "best": None,
        }


@app.post("/timeline")
def timeline_endpoint(req: TimelineRequest):
    """Monthly price history plus a multi-horizon forecast for one crop.

    predict.timeline already degrades for crops with no history; this wrapper also catches
    anything unexpected, so a crop with no data can never surface as a 500.
    """
    as_of = req.asOf or date.today()
    try:
        return predict.timeline(req.cropId, as_of, req.months)
    except Exception:
        return {
            "cropId": str(req.cropId).lower(),
            "cropName": None,
            "asOf": as_of.isoformat(),
            "activePredictor": "unavailable",
            "confidence": "Low",
            "reasonCode": "unavailable",
            "reasonParams": {},
            "modelVersion": predict.model_info().get("version"),
            "explanation": "No forecast available for this crop yet "
                           "(insufficient historical data).",
            "history": [],
            "forecast": [],
        }


class IngestNewsRequest(BaseModel):
    """Orchestration knobs for the daily news pass.

    Defaults run the full live pipeline: fetch, write, QA, then score and write daily.
    """
    dryRun: bool = False
    skipQa: bool = False
    # The admin News feed shows SentimentScore and Topics per article, so write them back.
    writebackScores: bool = True


@admin_router.post("/ingest-news")
def ingest_news_endpoint(req: IngestNewsRequest):
    """Run the daily news pipeline: ingest RSS, then score sentiment.

    Called by the .NET Ingestion Worker once per daily pass. The two stages run in order and
    each failure surfaces as a structured 502 so the Worker can log it and continue. The
    news scripts are imported lazily, so a breakage there cannot block serving startup or
    the /predict path.
    """
    # Lazy import: keep serving startup independent of the news modules.
    try:
        import ingest_news
        import score_news
    except Exception:  # pragma: no cover - import wiring guard
        _log.exception("News pipeline modules unavailable")
        raise HTTPException(
            status_code=503,
            detail="News pipeline module unavailable.",
        )

    try:
        ingest_summary = ingest_news.run(dry_run=req.dryRun, skip_qa=req.skipQa)
    except Exception:
        _log.exception("News ingestion (ingest_news) failed")
        raise HTTPException(
            status_code=502,
            detail="News ingestion failed.",
        )

    try:
        score_summary = score_news.run(
            dry_run=req.dryRun, writeback_scores=req.writebackScores
        )
    except Exception:
        _log.exception("News scoring (score_news) failed")
        raise HTTPException(
            status_code=502,
            detail="News scoring failed.",
        )

    return {
        "status": "ok",
        "ingest": ingest_summary,
        "score": score_summary,
    }


class IngestHartiRequest(BaseModel):
    """Orchestration knobs for the multi-market HARTI daily pass.

    The Worker sends its resume watermark minus a look-back window (default 7 days) as
    sinceDate (ISO 'YYYY-MM-DD'); only later bulletins are fetched, so the daily pass
    re-scans about a week to catch late-published bulletins without re-scraping the whole
    corpus. The upsert is idempotent, so the re-scan is free. sinceDate null means a full
    backfill, and noDownload / dryRun are for offline reruns and tests.

    A null sinceDate triggers a ~3000-PDF backfill that can exceed the Worker's HTTP
    timeout, so the first-time seed is the CLI 'python ingest_harti.py', which has none.
    After that the Worker only sends fast incremental calls.
    """
    sinceDate: Optional[str] = None
    noDownload: bool = False
    dryRun: bool = False


@admin_router.post("/ingest-harti")
def ingest_harti_endpoint(req: IngestHartiRequest):
    """Run the multi-market HARTI ingestion pass in-process.

    Called by the .NET Ingestion Worker. Runs the same steps as ingest_harti.py for the
    PriceObservations path (download bounded by sinceDate, parse, upsert) plus the
    data-quality hooks: a cross-source duplicate is a HARD FAIL surfaced as a 502 so the
    Worker does not advance its watermark, while healing, outlier flags and the gap report
    are report-only. The heavy module is imported lazily.
    """
    try:
        import ingest_harti_service
    except Exception:  # pragma: no cover - import wiring guard
        _log.exception("HARTI ingestion module unavailable")
        raise HTTPException(
            status_code=503,
            detail="HARTI ingestion module unavailable.",
        )

    try:
        return ingest_harti_service.run(
            since_date=req.sinceDate,
            no_download=req.noDownload,
            dry_run=req.dryRun,
        )
    except AssertionError:
        # The cross-source duplicate check failed: a data-integrity hard fail. Do not let the
        # Worker advance its watermark - surface a structured 502.
        _log.exception("HARTI ingestion: cross-source duplicate check FAILED")
        raise HTTPException(
            status_code=502,
            detail="HARTI ingestion failed data-quality gate (cross-source duplicates).",
        )
    except Exception:
        _log.exception("HARTI ingestion failed")
        raise HTTPException(
            status_code=502,
            detail="HARTI ingestion failed.",
        )


class IngestCbslRequest(BaseModel):
    """Orchestration knobs for the CBSL Daily Price Report pass.

    Mirrors IngestHartiRequest. sinceDate is the .NET watermark's exclusive lower bound;
    absent means the downloader's own last-7-days default, so an empty watermark can never
    trigger a silent full backfill. Deliberate backfills go through the ingest_cbsl.py CLI.
    """
    sinceDate: Optional[str] = None
    noDownload: bool = False
    dryRun: bool = False


@admin_router.post("/ingest-cbsl")
def ingest_cbsl_endpoint(req: IngestCbslRequest):
    """Run the CBSL Daily Price Report ingestion pass in-process.

    Called by the .NET Ingestion Worker: download (a 404 just means no report was published
    that day), parse, upsert into PriceObservations, then the duplicate check as a HARD FAIL
    surfaced as a 502 so the Worker does not advance its watermark. Lazy import.
    """
    try:
        import ingest_cbsl_service
    except Exception:  # pragma: no cover - import wiring guard
        _log.exception("CBSL ingestion module unavailable")
        raise HTTPException(
            status_code=503,
            detail="CBSL ingestion module unavailable.",
        )

    try:
        return ingest_cbsl_service.run(
            since_date=req.sinceDate,
            no_download=req.noDownload,
            dry_run=req.dryRun,
        )
    except AssertionError:
        _log.exception("CBSL ingestion: cross-source duplicate check FAILED")
        raise HTTPException(
            status_code=502,
            detail="CBSL ingestion failed data-quality gate (cross-source duplicates).",
        )
    except Exception:
        _log.exception("CBSL ingestion failed")
        raise HTTPException(
            status_code=502,
            detail="CBSL ingestion failed.",
        )


class IngestCbslMacroRequest(BaseModel):
    """Orchestration knobs for the monthly CBSL macro pass.

    noDownload and dryRun are for offline reruns and tests. `full` forces a
    full re-scan of the entire CCPI+MEI corpus, ignoring the DB watermark —
    for backfills/repairs only, default OFF.

    CORRECTED 2026-08 (see ingest_cbsl_macro.py's WRITE-BACK): the earlier
    "no sinceDate knob, a full re-scrape every pass is cheaper than watermark
    plumbing" reasoning here was WRONG in practice — a full re-scrape/re-parse
    every pass OOMKilled the k8s CronJob on its very first run and would only
    get worse as the corpus grows monthly forever. The pass is now
    watermark-driven by default (loader.get_watermarks() inside run()); `full`
    is the explicit escape hatch for when a genuine full re-scan is wanted.
    """
    noDownload: bool = False
    dryRun: bool = False
    full: bool = False


@admin_router.post("/ingest-cbsl-macro")
def ingest_cbsl_macro_endpoint(req: IngestCbslMacroRequest):
    """Run the CBSL macro ingestion pass in-process.

    Download, parse and upsert for both CBSL sources (CCPI press releases and MEI packs),
    returning a structured summary of artifacts, rows and per-series coverage. 'No new
    bulletin since the watermark' is a success with zero rows, never an error. The heavy
    module is imported lazily.

    HTTP status stays 200 even when the summary body's own `status` field is
    "partial" (one or more artifacts lost a DB write this pass, per-artifact
    isolated — see ingest_cbsl_macro.py's WRITE-BACK) -- this endpoint's own
    contract is "the pass ran and here is an honest report", the same as
    /admin/ingest-harti's gaps/outliers report-only fields; only a whole-pass
    exception (this route's own except below) is a 502, and only an import
    failure is a 503.

    THE HTTP CODE IS NOT THE WHOLE ANSWER, and there is now a caller that
    depends on that: .NET's CbslMacroIngestionService gates on this body's
    `status` and writes a FAILED IngestionRuns row for anything that is not
    exactly "ok" (missing/null included). So `status` is a load-bearing part
    of this response, not a decorative field -- never drop it, never rename
    it, and never widen its vocabulary beyond "ok"|"partial" without changing
    that caller in the same PR. Any other future caller wiring this into an
    automated gate must check `status` too, not just the HTTP code.
    """
    try:
        import ingest_cbsl_macro
    except Exception:  # pragma: no cover - import wiring guard
        _log.exception("CBSL macro ingestion module unavailable")
        raise HTTPException(
            status_code=503,
            detail="CBSL macro ingestion module unavailable.",
        )

    try:
        return ingest_cbsl_macro.run(
            no_download=req.noDownload,
            dry_run=req.dryRun,
            full=req.full,
        )
    except Exception:
        _log.exception("CBSL macro ingestion failed")
        raise HTTPException(
            status_code=502,
            detail="CBSL macro ingestion failed.",
        )


class SnapshotForecastsRequest(BaseModel):
    """Orchestration knobs for the nightly forecast-snapshot pass.

    snapshotDate is the plant date every forecast in the pass is made for; absent means
    today, which is what the Worker sends. It must be today or at most
    SNAPSHOT_MATURE_GRACE_DAYS old - enough to catch up a missed night, not enough to
    re-predict history with hindsight - and anything outside that is a 422. runMature also
    scores the snapshots whose harvest date has arrived; it is a separate switch only so a
    catch-up can write snapshot rows without a maturing sweep. dryRun computes and counts
    a full pass without writing anything.
    """
    snapshotDate: Optional[date] = None
    runMature: bool = True
    dryRun: bool = False


@admin_router.post("/snapshot-forecasts")
def snapshot_forecasts_endpoint(req: SnapshotForecastsRequest):
    """Run the forecast-snapshot pass (one prediction per crop) and the maturing pass.

    Called by trigger_forecast_snapshot.py (a Python entry point in the daily pipeline's
    build-features container), AFTER build_features.py has rebuilt CropFeatureDaily --
    see the PRD's 2026-07-27 CORRECTED note in 4.2: this is what the snapshot/mature
    passes actually read, and a caller earlier in the pipeline (the original spec's ".NET
    Worker, last in its pass") would predict and score against a feature store one
    pipeline-day stale. Per-crop failures are counted inside the summary and never fail
    the request; a rejected snapshotDate is a 422, and only an unexpected whole-pass
    failure surfaces as a 502, which the caller records without raising further (fail-soft
    -- PRD 3.7). The module is imported lazily so a breakage here cannot block serving
    startup or /predict.

    The response is the PRD 4.2 summary shape plus ONE added key, `snapshot.frozen`: the
    count of rows the pass deliberately left alone because they had already matured.
    Without it, inserted + updated no longer accounts for every attempted crop and a
    re-run over old days would report all-zeros indistinguishably from a no-op. The .NET
    mirror adopts the extended shape.
    """
    try:
        from . import snapshots
    except Exception:  # pragma: no cover - import wiring guard
        _log.exception("Forecast snapshot module unavailable")
        raise HTTPException(
            status_code=503,
            detail="Forecast snapshot module unavailable.",
        )

    try:
        return snapshots.run(
            snapshot_date=req.snapshotDate,
            run_mature=req.runMature,
            dry_run=req.dryRun,
        )
    except snapshots.SnapshotDateError as exc:
        # A refused request, not a failure: the caller asked for a date we will not
        # forecast for. The message is safe to return - it is about the caller's own
        # input and carries no internals.
        raise HTTPException(status_code=422, detail=str(exc))
    except Exception:
        _log.exception("Forecast snapshot pass failed")
        raise HTTPException(
            status_code=502,
            detail="Forecast snapshot pass failed.",
        )


# Register the protected admin routes (must come after the routes are declared).
app.include_router(admin_router)
