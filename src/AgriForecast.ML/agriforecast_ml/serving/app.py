"""FastAPI service exposing harvest-price predictions.

Internal service (called by the .NET API). No auth in this MVP — add an API
key / network isolation before any external exposure.
"""
from __future__ import annotations

from datetime import date
from typing import Optional

from fastapi import FastAPI
from pydantic import BaseModel, Field

from . import predict

app = FastAPI(title="AgriForecast ML — Model A", version="1.0")


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
