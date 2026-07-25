"""FastAPI service exposing harvest-price predictions.

Internal service (called by the .NET API). Public endpoints (/health,
/model-info, /predict, /timeline) are unauthenticated by design; all /admin/*
routes require an ``X-API-Key`` header matching the ``ML_ADMIN_API_KEY``
environment variable (see ``require_api_key`` / the ``admin_router`` below).
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

# Load secrets from the gitignored .env BEFORE anything reads the environment,
# so the service works regardless of how uvicorn was launched (run_ml.sh, IDE
# run config, or a bare `python -m uvicorn`). Real env vars take precedence.
load_env_file()

from . import predict  # noqa: E402  (env must be loaded first)

app = FastAPI(title="AgriForecast ML — Model A", version="1.0")

_log = logging.getLogger(__name__)

_ADMIN_API_KEY_ENV = "ML_ADMIN_API_KEY"


@app.exception_handler(Exception)
async def unhandled_exception_handler(request: Request, exc: Exception) -> JSONResponse:
    """Global backstop for any UNHANDLED exception in any route (P3, item 12).

    Every route already wraps its work in try/except and raises HTTPException
    with a fixed generic ``detail`` (never interpolating the exception). This
    handler is the safety net for anything those guards miss — a bug in a new
    route, or an error raised outside a route's try block. Without it an
    unhandled exception falls through to Starlette's default 500, which in a
    debug/dev configuration can leak a traceback / internal file paths.

    Contract:
    - Full exception (with traceback) is logged server-side only, tagged with
      the request method + path for triage. Request bodies and headers are
      NEVER logged — they can carry the admin ``X-API-Key``.
    - The client always receives a fixed generic 500 body. The exception, its
      type, and any path/URL are NEVER echoed to the caller.

    NOTE: FastAPI/Starlette route ``HTTPException`` through their own handler
    earlier in the stack, so this ``except Exception``-style handler does NOT
    intercept HTTPException — 401/502/503 raised in routes keep their intended
    status + detail. (Proven in the serving auth/error-leak test suite.)
    """
    _log.exception(
        "Unhandled exception serving %s %s", request.method, request.url.path
    )
    return JSONResponse(status_code=500, content={"detail": "Internal server error."})


def require_api_key(x_api_key: Optional[str] = Header(default=None)) -> None:
    """Auth dependency for /admin/* routes.

    Reads the expected key from the ``ML_ADMIN_API_KEY`` env var (never
    hardcoded/committed) and compares it to the caller's ``X-API-Key`` header
    in constant time. Fails loudly and safely:

    - Server misconfiguration (env var unset/empty) -> 500, NEVER allow-all.
      A missing server-side key must never mean "no auth required".
    - Missing or mismatched ``X-API-Key`` header -> 401.
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


# All /admin/* routes inherit require_api_key, so any future admin route is
# protected automatically.
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
    # Upper bound is one seasonal cycle: past that the frozen price/weather anchor
    # is too stale for the comparison to mean anything (predict enforces the same
    # cap, this just rejects nonsense at the edge).
    horizonDays: int = Field(default=90, ge=7, le=365)


@app.get("/health")
def health():
    return {"status": "ok"}


@app.get("/model-info")
def model_info():
    return predict.model_info()


@app.get("/crop-readiness")
def crop_readiness_endpoint():
    """Per-crop readiness map for the app's crop-status colouring (UI 2026-07-22).

    Defensive like /timeline: any unexpected failure returns the honest empty
    shape (modelActive=false, no crops -> the UI shows no tint), never a 500.
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
    """Best planting/harvest window for one crop — ranks candidate planting dates.

    Defensive like /timeline and /crop-readiness: the honest answer to "we could
    not work this out" is rankable=false with a reason, NEVER a 500 and never a
    fabricated window. predict.harvest_window already returns that shape for every
    gate it knows about; this wrapper catches whatever it doesn't.
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
    """Monthly price history + multi-horizon forecast for one crop.

    `predict.timeline` already degrades gracefully for crops with no history
    (empty history list + global fallback + Low confidence). We still wrap it
    defensively so a crop with zero data can never surface as a 500 to the app:
    on any unexpected failure we return a safe empty-history / low-confidence
    shape instead.
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

    Defaults run the full live pipeline (fetch + write + QA, then score +
    write daily). The .NET Worker calls this with defaults once per pass.
    """
    dryRun: bool = False
    skipQa: bool = False
    # Per-article SentimentScore + Topics writeback is the default: the admin
    # News feed (.NET /api/news-articles) surfaces those columns per article.
    writebackScores: bool = True


@admin_router.post("/ingest-news")
def ingest_news_endpoint(req: IngestNewsRequest):
    """Run the daily news pipeline: ingest RSS, then score sentiment.

    Internal admin endpoint orchestrated by the .NET Ingestion Worker (4th
    step of each daily pass), consistent with how the Worker already drives
    market/weather/economic ingestion. The two stages are run in order and
    each failure surfaces as a structured 502 so the Worker can log it and
    continue, rather than the call leaking an unhandled 500.

    The news scripts live at the ML repo root (ingest_news.py / score_news.py),
    imported lazily so any news-module breakage can never block serving startup
    or the /predict path.
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

    The .NET Worker sends its resume watermark (minus a late-arrival look-back
    window, default 7 days) as ``sinceDate`` (ISO 'YYYY-MM-DD'); only bulletins
    strictly AFTER it are fetched/parsed, so the daily pass does not re-scrape
    the whole corpus while still re-scanning ~the last week to catch a bulletin
    published late for an already-passed date (the upsert is idempotent, so the
    re-scan is free). ``sinceDate`` null => full backfill. ``noDownload``/
    ``dryRun`` are for offline reruns and tests.

    FIRST-RUN BOOTSTRAP: a null ``sinceDate`` triggers a full ~3000-PDF backfill
    that can run for many minutes and may exceed the Worker's HTTP timeout
    (MlService:HartiIngestTimeoutSeconds, default 1800s), so the cold first run
    may never complete over HTTP. The sanctioned first-time seed is to run the
    CLI ``python ingest_harti.py`` once (it has NO HTTP timeout); after that this
    endpoint only ever gets incremental, look-back-bounded ``sinceDate`` calls
    from the Worker, which are fast.
    """
    sinceDate: Optional[str] = None
    noDownload: bool = False
    dryRun: bool = False


@admin_router.post("/ingest-harti")
def ingest_harti_endpoint(req: IngestHartiRequest):
    """Run the multi-market HARTI ingestion pass in-process.

    Internal admin endpoint orchestrated by the .NET Ingestion Worker (R1.1 P1
    Step 6), consistent with /admin/ingest-news. Runs the same steps as
    ingest_harti.py for the PriceObservations path (download-bounded-by-sinceDate
    -> parse -> upsert), plus the data-quality hooks:
      * assert_no_source_duplicates -> HARD FAIL: surfaced as a 502 so the Worker
        logs it and does NOT advance its watermark.
      * heal_price_observation_crops + flag_price_outliers + gap_report ->
        post-pass summaries returned in the response (report-only; they never
        gate the pass).

    The heavy work module is imported lazily so any HARTI-module breakage can
    never block serving startup or the /predict path.
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
        # Cross-source duplicate check failed — a data-integrity hard fail. Do NOT
        # let the Worker advance its watermark; surface a structured 502.
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

    Mirrors IngestHartiRequest. ``sinceDate`` is the .NET watermark's resume
    lower bound (exclusive); ABSENT/None => the downloader's own last-7-days
    default (capture-only contract, 2026-07-22: an empty watermark must never
    trigger a silent full backfill — deliberate backfills go through the
    ingest_cbsl.py CLI with an explicit --since / --max-dates).
    """
    sinceDate: Optional[str] = None
    noDownload: bool = False
    dryRun: bool = False


@admin_router.post("/ingest-cbsl")
def ingest_cbsl_endpoint(req: IngestCbslRequest):
    """Run the CBSL Daily Price Report ingestion pass in-process.

    Internal admin endpoint orchestrated by the .NET Ingestion Worker
    (CbslPriceReportIngestionService), consistent with /admin/ingest-harti:
    download (deterministic per-date URL; 404 = no report published = normal
    gap) -> parse -> upsert into PriceObservations, then
    assert_no_source_duplicates as a HARD FAIL surfaced as a 502 so the Worker
    logs it and does NOT advance its watermark. Lazy import so any CBSL-module
    breakage can never block serving startup or /predict.
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

    Mirrors IngestHartiRequest's shape. ``noDownload``/``dryRun`` are for
    offline reruns and tests. There is no ``sinceDate`` knob: both CBSL
    listings (CCPI press releases, MEI packs) are small, bounded corpora
    (tens of PDFs total) rather than HARTI's ~3000-PDF daily-bulletin corpus,
    so a full re-scrape + idempotent re-upsert every pass is cheap and simple
    — no incremental watermark plumbing needed here.
    """
    noDownload: bool = False
    dryRun: bool = False


@admin_router.post("/ingest-cbsl-macro")
def ingest_cbsl_macro_endpoint(req: IngestCbslMacroRequest):
    """Run the CBSL macro ingestion pass in-process.

    Internal admin endpoint (P3, ClickUp 86cahefbh), consistent with
    /admin/ingest-harti and /admin/ingest-news. Runs download -> parse ->
    upsert for both CBSL sources (CCPI press releases + MEI packs) and
    returns a structured summary (artifacts fetched/skipped, rows
    inserted/updated, per-series coverage). "No new bulletin since watermark"
    is a SUCCESS with zero rows, never an error.

    The heavy work module is imported lazily so any CBSL-module breakage can
    never block serving startup or the /predict path.
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
        )
    except Exception:
        _log.exception("CBSL macro ingestion failed")
        raise HTTPException(
            status_code=502,
            detail="CBSL macro ingestion failed.",
        )


# Register the protected admin routes (must come after the routes are declared).
app.include_router(admin_router)
