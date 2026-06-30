"""FastAPI service exposing harvest-price predictions.

Internal service (called by the .NET API). No auth in this MVP — add an API
key / network isolation before any external exposure.
"""
from __future__ import annotations

import logging
from datetime import date
from typing import Optional

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

from . import predict

app = FastAPI(title="AgriForecast ML — Model A", version="1.0")

_log = logging.getLogger(__name__)


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


@app.post("/admin/ingest-news")
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
    except Exception as exc:  # pragma: no cover - import wiring guard
        _log.exception("News pipeline modules unavailable")
        raise HTTPException(
            status_code=503,
            detail=f"News pipeline unavailable: {type(exc).__name__}: {exc}",
        )

    try:
        ingest_summary = ingest_news.run(dry_run=req.dryRun, skip_qa=req.skipQa)
    except Exception as exc:
        _log.exception("News ingestion (ingest_news) failed")
        raise HTTPException(
            status_code=502,
            detail=f"ingest_news failed: {type(exc).__name__}: {exc}",
        )

    try:
        score_summary = score_news.run(
            dry_run=req.dryRun, writeback_scores=req.writebackScores
        )
    except Exception as exc:
        _log.exception("News scoring (score_news) failed")
        raise HTTPException(
            status_code=502,
            detail=f"score_news failed: {type(exc).__name__}: {exc}",
        )

    return {
        "status": "ok",
        "ingest": ingest_summary,
        "score": score_summary,
    }
