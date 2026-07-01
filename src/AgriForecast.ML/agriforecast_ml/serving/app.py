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

from fastapi import APIRouter, Depends, FastAPI, Header, HTTPException
from pydantic import BaseModel, Field

from . import predict

app = FastAPI(title="AgriForecast ML — Model A", version="1.0")

_log = logging.getLogger(__name__)

_ADMIN_API_KEY_ENV = "ML_ADMIN_API_KEY"


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


@app.get("/health")
def health():
    return {"status": "ok"}


@app.get("/model-info")
def model_info():
    return predict.model_info()


@app.post("/predict")
def predict_endpoint(req: PredictRequest):
    return predict.predict_harvest(req.cropId, req.plantDate)


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
    writebackScores: bool = False


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


# Register the protected admin routes (must come after the routes are declared).
app.include_router(admin_router)
